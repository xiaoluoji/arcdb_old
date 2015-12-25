﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using SharpMysql;
using SharpConfig;
using Murmur;
using System.IO;
using ArticleCollect;
using System.Diagnostics;
using System.Runtime.Remoting.Messaging;
using System.Threading;
using System.Collections;
using System.Collections.Concurrent;

namespace ArcDB
{
    public partial class CoArticleForm : Form
    {
        //private List<Dictionary<string, object>> _articleCollections;                           
        private ConcurrentDictionary<long, Dictionary<string, object>> _articleCollections; //采集对象集合，每一个集合中包括一个ArticleCollectOffline采集对象， 一个监控耗时的stopwatch对象
        private Dictionary<long, string> _dicCids;                                                       //采集规则ID和采集名称集合，
        private string _connString;                                                                               //数据库连接字符串
        System.Threading.Timer _timerUpdateForm;                                                  //listViewCoArticles状态更新定时器
        private Queue<long> _queueCids;                                                                  //采集规则ID队列
        Stopwatch swGlobal = new Stopwatch();                                                        //监控总任务时间
        private List<string> _hashList;                                                                         //用来检测采集文章URL HASH是否重复的集合
        private int _cfgPicNum=0;                                                                               //保存数据库图片总数，用来做图片分表处理
        private string _cfgBasePath="";                                                                       //图片保存根目录
        private string _cfgImgBaseurl="";                                                                   //图片网址所使用的域名

        public CoArticleForm(string connString, Dictionary<long, string> dicCids)
        {
            CheckForIllegalCrossThreadCalls = false;
            InitializeComponent();
            _articleCollections = new ConcurrentDictionary<long, Dictionary<string, object>>();
            _dicCids = dicCids;
            _connString = connString;
        }
        //采集窗口要关闭时的处理
        private void CoArticleForm_Closing(object sender, FormClosingEventArgs e)
        {
            if (MessageBox.Show("关闭当前窗口将取消所有正在运行中的采集，是否继续？", "询问", MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                e.Cancel = false;
            }
            else
            {
                cancelAllTask();
                e.Cancel = true;
            }
        }
        //采集窗口关闭后的处理
        private void CoArticleForm_Closed(object sender, FormClosedEventArgs e)
        {
            cancelAllTask();
        }

        //用来监控当前并行采集中的各个进程的进度状态
        private void updateForm(object state)
        {
            foreach (var collectItem in _articleCollections)
            {
                try
                {
                    Dictionary<string, object> dic = collectItem.Value;
                    long currentCid = collectItem.Key;
                    ArticleCollectOffline currentCollectWork = (ArticleCollectOffline)dic["collect"];
                    if (currentCollectWork.CoState != "采集结束" && currentCollectWork.CancelException == null && dic != null)
                    {
                        Stopwatch currentWatch = (Stopwatch)dic["watch"];
                        ListViewItem currentItem = listViewCoArticles.Items.Cast<ListViewItem>().First(item => item.SubItems[1].Text == currentCid.ToString());
                        listViewCoArticles.BeginUpdate();
                        if (currentCollectWork.CoState != "")
                        {
                            currentItem.SubItems[0].Text = currentCollectWork.CoState;
                        }
                        currentItem.SubItems[3].Text = currentCollectWork.CurrentProcessedListPages.ToString();
                        currentItem.SubItems[4].Text = currentCollectWork.CurrentGetArticlePages.ToString();
                        currentItem.SubItems[5].Text = currentCollectWork.CurrentNeedConums.ToString();
                        currentItem.SubItems[6].Text = currentCollectWork.CurrentProcessedArticles.ToString();
                        currentItem.SubItems[7].Text = currentCollectWork.CurrentSavedArticles.ToString();
                        string timeUsed = currentWatch.Elapsed.ToString();
                        currentItem.SubItems[8].Text = timeUsed.Remove(8, 8);
                        listViewCoArticles.EndUpdate();
                    }
                    else if (dic == null)
                    {
                        tboxErrorOutput.AppendText(string.Format("ID: {0} NULL \n", currentCid));
                    }
                    if (_articleCollections.Count > 0)
                    {
                        labTime.Text = string.Format("总共耗时： {0}\n", swGlobal.Elapsed.ToString());
                    }
                    else
                    {
                        swGlobal.Stop();
                    }
                }
                catch (Exception)
                {

                }
            }
        }

        //取消所有采集任务
        private void cancelAllTask()
        {
            _queueCids.Clear();
            foreach (var collectItem in _articleCollections)
            {
                try
                {
                    Dictionary<string, object> dic = collectItem.Value;
                    long currentCid = collectItem.Key;
                    ArticleCollectOffline currentCollectWork = (ArticleCollectOffline)dic["collect"];
                    if (currentCollectWork.CoState!="保存文章") //如果采集正在保存文章至数据库则不能取消
                    {
                        currentCollectWork.CancelToken.Cancel();
                    }
                }
                catch (Exception)
                {

                }
            }
        }
        //取消当前采集任务
        private void cancelCurrentTask()
        {
            foreach (var collectItem in _articleCollections)
            {
                try
                {
                    Dictionary<string, object> dic = collectItem.Value;
                    long currentCid = collectItem.Key;
                    ArticleCollectOffline currentCollectWork = (ArticleCollectOffline)dic["collect"];
                    if (currentCollectWork.CoState != "保存文章")//如果采集正在保存文章至数据库则不能取消
                    {
                        currentCollectWork.CancelToken.Cancel();
                    }
                }
                catch (Exception)
                {

                }
            }
        }

        //检查必填项是否正确填写，这里暂时只是用 string.IsNullOrWhiteSpace()判断是否为空值，未做进一步校验，以后完善
        private bool validateCoConfig(Dictionary<string, string> coConfigs)
        {
            foreach (KeyValuePair<string, string> kvp in coConfigs)
            {
                if (string.IsNullOrWhiteSpace(kvp.Value))
                {
                    return false;
                }
            }
            try
            {
                int startPageNumber = int.Parse(coConfigs["start_page_number"]);
                int stopPageNumber = int.Parse(coConfigs["stop_page_number"]);
                int subPageStartNum = int.Parse(coConfigs["arc_subpage_startnum"]);
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }

        //初始化listViewCoArticles表单，填充表单中的 采集状态，采集规则ID和采集规则名称
        private void initializeForm()
        {
            _queueCids = new Queue<long>();
            listViewCoArticles.BeginUpdate();
            foreach (KeyValuePair<long, string> kvp in _dicCids)
            {
                string[] subItems = new string[] { "待采集", kvp.Key.ToString(), kvp.Value, "0", "0", "0", "0", "0" };
                ListViewItem listItem = new ListViewItem(subItems);
                listViewCoArticles.Items.Add(listItem);
                _queueCids.Enqueue(kvp.Key);
            }
            listViewCoArticles.EndUpdate();
            listViewCoArticles.AutoResizeColumns(ColumnHeaderAutoResizeStyle.HeaderSize);
            listViewCoArticles.GridLines = true;
        }

        //将多行文本转换成List<string>类型的对象返回
        private List<string> getLines(string text)
        {
            System.Windows.Forms.TextBox tempTbox = new System.Windows.Forms.TextBox();
            tempTbox.Multiline = true;
            tempTbox.Text = text;
            string[] linesArr = tempTbox.Lines;
            return linesArr.ToList<string>();
        }
        //从需要执行的采集Cid队列中获取一条CID记录
        private object queueLock = new object();
        private long getOneCid()
        {
            long cid = -1;
            lock (queueLock)
            {
                if (_queueCids.Count > 0)
                {
                    cid = _queueCids.Dequeue();
                }
            }
            return cid;
        }
        //从 _articleCollections监控采集状态集合中移出一条已经完成采集的记录
        private void removeOneCollection(long cid)
        {
            Dictionary<string, object> oneCollection = null;
            bool removed = false;
            do
            {
                removed = _articleCollections.TryRemove(cid, out oneCollection);
            } while (!removed);
        }


        //将传入的字符串通过murmurhash函数生成32位长的字符串
        private static string GetHashAsString(string stringToHash)
        {
            Murmur128 urlHash = MurmurHash.Create128(managed: false);
            byte[] urlbyte = System.Text.Encoding.UTF8.GetBytes(stringToHash);
            byte[] hash = urlHash.ComputeHash(urlbyte);

            //以下代码也可以用 BitConverter.ToString(hash)代替
            var builder = new StringBuilder(16);
            for (int i = 0; i < hash.Length; i++)
                builder.Append(hash[i].ToString("x2"));

            return builder.ToString();
        }

        //检查文章是否已经采集过, 将采集过的URL记录从采集对象中的文章页集合中剔除
        private object hashLock = new object();
        private void removeDumpArcpages(ArticleCollectOffline collectOffline)
        {
            List<string> articleNeedCoPages = new List<string>();
            List<string> currentGetArcPages = collectOffline.CorrectArticlePages;
            if (_hashList == null)
            {
                mySqlDB myDB = new mySqlDB(_connString);
                string sResult = "";
                int counts = 0;
                string sql = "select hash from arc_contents";

                List<Dictionary<string, object>> hashDicList = myDB.GetRecords(sql, ref sResult, ref counts);
                if (sResult == mySqlDB.SUCCESS && counts > 0)
                {
                    _hashList = new List<string>();
                    foreach (var item in hashDicList)
                    {
                        lock (hashLock)
                        {
                            _hashList.Add(item["hash"].ToString());
                        }
                    }
                }
            }
            if (_hashList != null)
            {
                foreach (var arcUrl in currentGetArcPages)
                {
                    if (!_hashList.Contains(GetHashAsString(arcUrl)))
                    {
                        articleNeedCoPages.Add(arcUrl);
                    }
                }
            }
            collectOffline.CorrectArticlePages = articleNeedCoPages;
        }

        //从数据库获取相关的配置信息
        private bool getSysConfig(ArticleCollectOffline collectOffline)
        {
            mySqlDB myDB = new mySqlDB(_connString);
            string sResult = "";
            int counts = 0;
            string sql = "";
            List<Dictionary<string, object>> dbResult;
            //读取保存图片的根目录变量
            if (_cfgBasePath=="")
            {
                sql = "select value from sys_config where varname='cfg_basepath'";
                dbResult = myDB.GetRecords(sql, ref sResult, ref counts);
                if (sResult == mySqlDB.SUCCESS && counts > 0)
                {
                    _cfgBasePath = dbResult[0]["value"].ToString();
                }
                else
                    return false;
            }
            //读取图片总数变量
            if (_cfgPicNum ==0)
            {
                sql = "select value from sys_config where varname='cfg_pic_num'";
                dbResult = myDB.GetRecords(sql, ref sResult, ref counts);
                if (sResult == mySqlDB.SUCCESS && counts > 0)
                {
                    _cfgPicNum = int.Parse(dbResult[0]["value"].ToString());
                }
                else
                    return false;
            }
            //读取base URL变量
            if (_cfgImgBaseurl=="")
            {
                sql = "select value from sys_config where varname='cfg_img_baseurl'";
                dbResult = myDB.GetRecords(sql, ref sResult, ref counts);
                if (sResult == mySqlDB.SUCCESS && counts > 0)
                {
                    _cfgImgBaseurl = dbResult[0]["value"].ToString();
                }
                else
                    return false;
            }
            if (_cfgBasePath==""||_cfgImgBaseurl=="")
            {
                return false;
            }
            return true;
        }
        //获取文章内容中的图片路径
        private List<string>getImgPath(string content, ArticleCollectOffline collectOffline)
        {
            HtmlAgilityPack.HtmlDocument doc = new HtmlAgilityPack.HtmlDocument();
            List<string> imgPathList = new List<string>();
            try
            {
                doc.LoadHtml(content);
                HtmlAgilityPack.HtmlNodeCollection imgNodes = doc.DocumentNode.SelectNodes("//img");
                foreach (HtmlAgilityPack.HtmlNode imgNode in imgNodes)
                {
                    string imgPath = imgNode.Attributes["src"].Value;
                    imgPathList.Add(imgPath);
                }
            }
            catch (Exception ex)
            {
                List<Exception> coException = collectOffline.CoException;
                coException.Add(ex);
            }
            return imgPathList;
        }

        //更新数据系统配置表中的图片总数参数
        private bool updateCfgPicnum(ArticleCollectOffline collectOffline)
        {
            mySqlDB myDB = new mySqlDB(_connString);
            string sResult = "";
            int counts = 0;
            string sql = "update sys_config set value='" + _cfgPicNum.ToString() + "' where varname='cfg_pic_num'";
            counts = myDB.executeDMLSQL(sql, ref sResult);
            if (sResult==mySqlDB.SUCCESS && counts>0)
            {
                return true;
            }
            else
            {
                List<Exception> coException = collectOffline.CoException;
                Exception mysqlError = new Exception(sResult);
                coException.Add(mysqlError);
                return false;
            }
        }

        //将文章保存进数据库,以及对文章内容中的图片做相关处理，复制到新的路径，以及生成最终的网络访问URL
        //private object picNumLock = new object();
        private void saveArticles(ArticleCollectOffline collectOffline)
        {
            collectOffline.CoState = "保存文章";
            long cid = collectOffline.Cid;

            if (getSysConfig(collectOffline))  //正确获取相关配置参数后再进行下一步处理
            {
                string typeName = collectOffline.TypeName;
                string sourceSite = collectOffline.SourceSite;
                int typeNameID = 0;  //采集分类ID
                int sourceSiteID = 0;  //来源网址ID
                mySqlDB myDB = new mySqlDB(_connString);
                string sResult = "";
                int counts = 0;
                //获取当前采集分类ID，如果数据库不存在则将当前采集分类插入数据库
                List<Dictionary<string, object>> dbResult;
                string sql = "select tid from arc_type where type_name='" + typeName + "'";
                dbResult = myDB.GetRecords(sql, ref sResult, ref counts);
                if (sResult == mySqlDB.SUCCESS && counts > 0)
                {
                    typeNameID = (int)dbResult[0]["tid"];
                }
                else
                {
                    sql = "insert into arc_type (type_name) values('" + typeName + "')";
                    counts = myDB.executeDMLSQL(sql, ref sResult);
                    if (sResult == mySqlDB.SUCCESS && counts > 0)
                    {
                        typeNameID = (int)myDB.LastInsertedId;
                    }
                }
                sResult = "";
                counts = 0;
                //获取当前采集来源网址ID，如果数据库不存在则将当前采集分类插入数据库
                sql = "select id from source_site where source_site='" + sourceSite + "'";
                dbResult = myDB.GetRecords(sql, ref sResult, ref counts);
                if (sResult == mySqlDB.SUCCESS && counts > 0)
                {
                    sourceSiteID = (int)dbResult[0]["id"];
                }
                else
                {
                    sql = "insert into source_site (source_site) values('" + sourceSite + "')";
                    counts = myDB.executeDMLSQL(sql, ref sResult);
                    if (sResult == mySqlDB.SUCCESS && counts > 0)
                    {
                        sourceSiteID = (int)myDB.LastInsertedId;
                    }
                }
                if (typeNameID != 0 && sourceSiteID != 0)  //必须要正确获得 typeNameID 和 sourceSiteID后才进行下一步的文章和图片的处理
                {
                    List<Dictionary<string, string>> articles = collectOffline.Articles;
                    var arcList = from d in articles
                                  orderby d["title"]
                                  ascending
                                  select d;
                    foreach (Dictionary<string, string> article in arcList)
                    {
                        sResult = "";
                        counts = 0;
                        string arcTitle = article["title"];
                        string arcUrl = article["url"];
                        string arcContent = article["content"];
                        string hash = GetHashAsString(arcUrl);
                        int aid = 0;
                        sql = "insert into arc_contents (type_id,cid,title,source_site,content,url,hash) values ('" + typeNameID.ToString() + "'";
                        sql = sql + ",'" + cid.ToString() + "'";
                        sql = sql + ",'" + arcTitle + "'";
                        sql = sql + ",'" + sourceSite + "'";
                        sql = sql + ",'" + arcContent + "'";
                        sql = sql + ",'" + arcUrl + "'";
                        sql = sql + ",'" + hash + "')";
                        counts = myDB.executeDMLSQL(sql, ref sResult);
                        if (sResult == mySqlDB.SUCCESS && counts > 0)
                        {
                            aid = (int)myDB.LastInsertedId;
                        }
                        else   //如果插入文章内容出错，则将错误信息记录下来到当前采集对象中
                        {
                            List<Exception> coException = collectOffline.CoException;
                            Exception mysqlError = new Exception(sResult);
                            coException.Add(mysqlError);
                        }
                        if (aid != 0)
                        {
                            collectOffline.CurrentSavedArticles++;
                            List<string> imgPathList = getImgPath(arcContent, collectOffline); //获取文章中的所有图片路径
                            foreach (string imgPath in imgPathList)  //循环处理文章中包含的图片，将图片复制到新的路径，用于图片服务器访问，生成图片最终用于网络访问的URL
                            {
                                string fileExtenstion = Path.GetExtension(imgPath);
                                sResult = "";
                                counts = 0;
                                string picFilePath = _cfgBasePath + @"src\"; //用来保存采集的图片要存储在采集服务器上的路径；
                                int firstSubDirNum = 0;  //一级子目录编号，同时也是图片域名的子域名编号
                                int secondSubDirNum = 0;  //二级子目录编号
                                firstSubDirNum = _cfgPicNum / 100000;
                                secondSubDirNum = _cfgPicNum % 100000 / 10000;
                                picFilePath = picFilePath + firstSubDirNum.ToString() + @"\" + secondSubDirNum;
                                if (!Directory.Exists(picFilePath))
                                {
                                    Directory.CreateDirectory(picFilePath);
                                }
                                string randomFileName = Path.GetRandomFileName();
                                string picFileName = picFilePath + @"\" + randomFileName + fileExtenstion;
                                string imgUrlPath = @"http://img" + firstSubDirNum.ToString() + "." + _cfgImgBaseurl + "/" + secondSubDirNum.ToString() + "/";
                                string imgUrl = imgUrlPath + randomFileName + fileExtenstion;
                                while (File.Exists(picFileName))  //随机生成新的图片文件名，如果随机文件名重复则要反复生成，直到不重复为止
                                {
                                    randomFileName = Path.GetRandomFileName();
                                    picFileName= picFilePath + @"\" + randomFileName + fileExtenstion;
                                    imgUrl= imgUrlPath + randomFileName + fileExtenstion;
                                }
                                try
                                {
                                    File.Copy(imgPath, picFileName);   //将源图片复制到新的路径中，用于图片服务器访问
                                    sql = "insert into arc_pics(cid,aid,ssid,pic_path,source_path,pic_url) values ('" + cid.ToString() + "'";
                                    sql = sql + ",'" + aid.ToString() + "'";
                                    sql = sql + ",'" + sourceSiteID.ToString() + "'";
                                    sql = sql + ",'" + picFileName + "'";
                                    sql = sql + ",'" + imgPath + "'";
                                    sql = sql + ",'" + imgUrl + "')";
                                    counts = myDB.executeDMLSQL(sql, ref sResult);
                                    if (sResult==mySqlDB.SUCCESS && counts>0)
                                    {
                                        _cfgPicNum++;
                                        if (!updateCfgPicnum(collectOffline))   //如果更新数据系统配置表中的图片总数参数失败的话，就退出当前处理过程，不然的话会导致图片总数出问题。
                                        {
                                            return;
                                        }
                                        arcContent = arcContent.Replace(imgPath, imgUrl);
                                    }
                                }
                                catch (Exception ex) //如果复制图片过程中出错的话，保存出错异常，退出当前处理过程
                                {
                                    List<Exception> coException = collectOffline.CoException;
                                    coException.Add(ex);
                                    return;
                                }
                            } //循环处理文章中的图片结束
                            sResult = "";
                            counts = 0;
                            sql = "update arc_contents set content='" + arcContent + "' where aid='" + aid.ToString() + "'";
                            counts = myDB.executeDMLSQL(sql, ref sResult);
                            if (sResult!=mySqlDB.SUCCESS) //如果更新文章内容出错，则将错误信息记录下来到当前采集对象中
                            {
                                List<Exception> coException = collectOffline.CoException;
                                Exception mysqlError = new Exception(sResult);
                                coException.Add(mysqlError);
                                return;
                            }

                        }  //判断文章是否正确插入到数据库结束

                    }  //循环处理文章结束
                }
            }

            collectOffline.CoState = "采集结束";
            //保存文章结束后开始下一个采集任务
            removeOneCollection(cid);   //从监控列表中移除保存完毕的采集对象
            ThreadPool.QueueUserWorkItem(startOneTask, null);
        }


        private delegate ArticleCollectOffline CollectProcess(ArticleCollectOffline collectOffline);

        //获得列表页清单
        private ArticleCollectOffline ProcessListPages(ArticleCollectOffline collectOffline)
        {
            collectOffline.ProcessListPages();
            return collectOffline;
        }
        //获得文章页清单
        private ArticleCollectOffline ProcessArticlePages(ArticleCollectOffline collectOffline)
        {
            collectOffline.ProcessArticlePages();
            return collectOffline;
        }
        //采集文章内容
        private ArticleCollectOffline ProcessCollectArticles(ArticleCollectOffline collectOffline)
        {
            collectOffline.ProcessCollectArticles();
            return collectOffline;
        }

        //异步执行获取列表文档结束
        private void ProcessListPagesComplete(IAsyncResult itfAR)
        {
            //异步执行获取列表文档完毕后，获得异步返回的结果，继续异步执行下一步（获取文章URL集合）
            CollectProcess collectProcessListPages = (CollectProcess)((AsyncResult)itfAR).AsyncDelegate;
            ArticleCollectOffline collectOffline = collectProcessListPages.EndInvoke(itfAR);
            if (collectOffline.CancelException == null)
            {
                CollectProcess collectProcessArticlePages = new CollectProcess(ProcessArticlePages);
                collectProcessArticlePages.BeginInvoke(collectOffline, ProcessArticlePagesComplete, null);
            }
            else
            {
                try
                {
                    tboxErrorOutput.AppendText(string.Format("终止获取列表页：{0} \n", collectOffline.CancelException.Message));
                    tboxErrorOutput.AppendText(string.Format("当前获取列表页位置：{0}\n", collectOffline.CurrentProcessedListPages));
                    tboxErrorOutput.AppendText(string.Format("总共需要处理列表页面数：{0}\n", collectOffline.CancelException.Data["TotalListPages"]));
                }
                catch (Exception)
                {
                }

                long cid = collectOffline.Cid;
                removeOneCollection(cid);
                ThreadPool.QueueUserWorkItem(startOneTask, null);

            }

            //输出列表文档的信息
            /*
            List<string> listPages = collectOffline.ListPages;
            tboxErrorOutput.AppendText(string.Format("本次获取列表页面数：{0}\n", listPages.Count));
            */
        }

        //异步执行获取文章URL集合结束
        private void ProcessArticlePagesComplete(IAsyncResult itfAR)
        {
            //异步执行获取文章URL集合完毕后，获得异步返回的结果，继续异步执行下一步（采集文档内容）
            CollectProcess collectProcessArticlePages = (CollectProcess)((AsyncResult)itfAR).AsyncDelegate;
            ArticleCollectOffline collectOffline = collectProcessArticlePages.EndInvoke(itfAR);
            if (collectOffline.CancelException == null)
            {
                //去除当前采集对象中已经采集过的文章URL
                removeDumpArcpages(collectOffline); 
                //执行下一步采集文章操作
                CollectProcess collectProcessCollectArticles = new CollectProcess(ProcessCollectArticles);
                collectProcessCollectArticles.BeginInvoke(collectOffline, ProcessCollectArticlesComplete, null);
            }
            else
            {
                try
                {
                    tboxErrorOutput.AppendText(string.Format("终止获取列表页：{0} \n", collectOffline.CancelException.Message));
                    tboxErrorOutput.AppendText(string.Format("当前处理列表页位置：{0}\n", collectOffline.CurrentProcessedListPages));
                    tboxErrorOutput.AppendText(string.Format("总共需要处理列表页面数：{0}\n", collectOffline.CancelException.Data["TotalListPages"]));
                    tboxErrorOutput.AppendText(string.Format("当前处理文章链接数：{0}\n", collectOffline.CurrentGetArticlePages));
                }
                catch (Exception)
                {
                }
                long cid = collectOffline.Cid;
                removeOneCollection(cid);
                ThreadPool.QueueUserWorkItem(startOneTask, null);
            }
            /*
            //输出URL集合信息
            List<string> correctListArticles = collectOffline.CorrectArticlePages;
            List<string> wrongListArticles = collectOffline.WrongArticlePages;
            tboxErrorOutput.AppendText("待采集文章链接：\n");
            foreach (string item in correctListArticles)
            {
                tboxErrorOutput.AppendText(string.Format("{0}\n", item));
            }
            tboxErrorOutput.AppendText("-------------------------------------------------------------------------------\n");
            tboxErrorOutput.AppendText("未能正确匹配内容链接，请检查匹配XPATH规则： \n");
            foreach (string item in wrongListArticles)
            {
                tboxErrorOutput.AppendText(string.Format("{0}\n", item));
            }
            */
        }

        //异步执行采集文章结束
        private void ProcessCollectArticlesComplete(IAsyncResult itfAR)
        {
            //异步执行采集文章内容完成后
            CollectProcess collectProcessCollectArticles = (CollectProcess)((AsyncResult)itfAR).AsyncDelegate; 
            ArticleCollectOffline collectOffline = collectProcessCollectArticles.EndInvoke(itfAR);
            //输出采集文档信息
            if (collectOffline.CancelException == null)
            {
                saveArticles(collectOffline);
            }
            else
            {
                try
                {
                    tboxErrorOutput.AppendText(string.Format("当前采集文章数：{0}\n", collectOffline.CurrentProcessedArticles));
                    tboxErrorOutput.AppendText(string.Format("此次总共需要采集文章数：{0}\n", collectOffline.CancelException.Data["TotalArticles"]));
                }
                catch (Exception)
                {
                }
                long cid = collectOffline.Cid;
                removeOneCollection(cid);
                ThreadPool.QueueUserWorkItem(startOneTask, null);
            }



            /*
            List<Exception> coException = collectOffline.CoException;
            printErrors(coException);
            List<Dictionary<string, string>> articles = collectOffline.Articles;

            tboxErrorOutput.AppendText(string.Format("采集文章总数：{0} \n", articles.Count));
            tboxErrorOutput.AppendText("-----------------------------------------------------------------------------------\n");
            var arcList = from d in articles
                          orderby d["title"]
                          ascending
                          select d;

            foreach (Dictionary<string, string> article in arcList)
            {
                foreach (KeyValuePair<string, string> kvp in article)
                {
                    tboxErrorOutput.AppendText(kvp.Key + ": \n");
                    tboxErrorOutput.AppendText(kvp.Value + "\n");
                }
                tboxErrorOutput.AppendText("---------------------------------------------\n");
            }
            */
        }

        private void printErrors(List<Exception> coExption)
        {
            int count = 0;
            foreach (Exception item in coExption)
            {
                count = count + 1;
                tboxErrorOutput.AppendText(string.Format("Error {0}: --------------------------\n", count));
                tboxErrorOutput.AppendText(string.Format("From: {0}   Message:{1}\n", item.TargetSite, item.Message));
                if (item.Data != null)
                {
                    foreach (DictionaryEntry de in item.Data)
                    {
                        tboxErrorOutput.AppendText(string.Format("{0} : {1} \n", de.Key, de.Value));
                    }
                }

            }
        }

        public void StartCoTask()
        {
            initializeForm();
            tboxErrorOutput.AppendText(string.Format("System.Environment.ProcessorCount: {0}\n", System.Environment.ProcessorCount));
            _timerUpdateForm = new System.Threading.Timer(
                updateForm,   //TimerCallBack委托对象
                              //PrintTime,
                null,                 //想传入的参数 （null表示没有参数）
                1000,                    //在开始之前，等待多长时间（以毫秒为单位）
                1000               //每次调用的间隔时间（以毫秒为单位）
                );

            swGlobal.Start();
            //采集任务队列只采用单进程来完成！！切记，否则存储采集文章时会出错
            //同时因为核心采集类中已经使用多线程进行采集，所以此处采用多线程来完成采集任务队列也并不能加快速度
            ThreadPool.QueueUserWorkItem(startOneTask,null);
        }

        private void startOneTask(object state)
        {
            long cid = getOneCid();
            if (cid!=-1)
            {
                mySqlDB myDB = new mySqlDB(_connString);
                int counts = 0;
                string sResult = "";
                string sql = "select * from co_config where cid = '" + cid + "'";
                List<Dictionary<string, object>> coConfigRecords = myDB.GetRecords(sql, ref sResult, ref counts);
                if (sResult == mySqlDB.SUCCESS && counts > 0)
                {
                    Dictionary<string, object> dicConfig = coConfigRecords[0];
                    Dictionary<string, string> checkFields = new Dictionary<string, string>();
                    checkFields.Add("co_name", dicConfig["co_name"].ToString());
                    checkFields.Add("source_lang", dicConfig["source_lang"].ToString());
                    checkFields.Add("type_name", dicConfig["type_name"].ToString());
                    checkFields.Add("source_site", dicConfig["source_site"].ToString());
                    checkFields.Add("co_offline", dicConfig["co_offline"].ToString());
                    checkFields.Add("list_path", dicConfig["list_path"].ToString());
                    checkFields.Add("start_page_number", dicConfig["start_page_number"].ToString());
                    checkFields.Add("stop_page_number", dicConfig["stop_page_number"].ToString());
                    checkFields.Add("xpath_arcurl_node", dicConfig["xpath_arcurl_node"].ToString());
                    checkFields.Add("xpath_title_node", dicConfig["xpath_title_node"].ToString());
                    checkFields.Add("xpath_content_node", dicConfig["xpath_content_node"].ToString());
                    checkFields.Add("arc_subpage_symbol", dicConfig["arc_subpage_symbol"].ToString());
                    checkFields.Add("arc_subpage_startnum", dicConfig["arc_subpage_startnum"].ToString());
                    if (!validateCoConfig(checkFields))
                    {
                        tboxErrorOutput.AppendText(string.Format("采集规则: (ID:{0}) 配置检查错误，请重新编辑采集规则项，确认必填项数据都已正确填写！ \n", cid));
                    }
                    else
                    {
                        string listPath = dicConfig["list_path"].ToString();
                        int listStartPageNum = int.Parse(dicConfig["start_page_number"].ToString());
                        int listStopPageNum = int.Parse(dicConfig["stop_page_number"].ToString());
                        string xpathArcurlNode = dicConfig["xpath_arcurl_node"].ToString();
                        string xpathTitleNode = dicConfig["xpath_title_node"].ToString();
                        string xpathContentNode = dicConfig["xpath_content_node"].ToString();
                        string arcSubPageSymbol = dicConfig["arc_subpage_symbol"].ToString();
                        int arcSubPageStartNum = int.Parse(dicConfig["arc_subpage_startnum"].ToString());
                        List<string> moreListPages = new List<string>();
                        List<string> subNodeParams = new List<string>();
                        List<string> regexParams = new List<string>();
                        if (!string.IsNullOrWhiteSpace(dicConfig["more_list_pages"].ToString()))
                        {
                            moreListPages = getLines(dicConfig["more_list_pages"].ToString());
                        }
                        if (!string.IsNullOrWhiteSpace(dicConfig["sub_node_params"].ToString()))
                        {
                            subNodeParams = getLines(dicConfig["sub_node_params"].ToString());
                        }
                        if (!string.IsNullOrWhiteSpace(dicConfig["regex_params"].ToString()))
                        {
                            regexParams = getLines(dicConfig["regex_params"].ToString());
                        }
                        CancellationTokenSource cancelToken = new CancellationTokenSource();
                        ArticleCollectOffline collectOffline = new ArticleCollectOffline(cid,listPath, listStartPageNum, listStopPageNum, xpathArcurlNode, xpathTitleNode, xpathContentNode, subNodeParams, regexParams, arcSubPageSymbol, arcSubPageStartNum);
                        if (moreListPages != null)
                        {
                            collectOffline.AddListPages(moreListPages);
                        }
                        collectOffline.CancelToken = cancelToken;
                        collectOffline.TypeName = dicConfig["type_name"].ToString();
                        collectOffline.SourceSite = dicConfig["source_site"].ToString();
                        Stopwatch sw = new Stopwatch();
                        sw.Start();
                        CollectProcess collectProcessListPages = new CollectProcess(ProcessListPages);
                        collectProcessListPages.BeginInvoke(collectOffline, ProcessListPagesComplete, null);

                        //创建新的Dictionary集合，其中包括采集对象，包括用来监控耗时的Stopwatch对象
                        Dictionary<string, object> oneCollect = new Dictionary<string, object>();
                        oneCollect.Add("collect", collectOffline);
                        oneCollect.Add("watch", sw);

                        //将当前采集对象添加到全局用来监控采集进程的采集对象集合中。
                        bool addResult = false;
                        do
                        {
                            addResult=_articleCollections.TryAdd(cid, oneCollect);
                        } while (!addResult);
                    }
                }
                else
                {
                    tboxErrorOutput.AppendText(string.Format("采集规则(ID:{0}) 读取数据库采集配置错误！：{1} \n", cid, sResult));
                }
            }

        }  //END Of StartOneTask

        private void btnCancelAll_Click(object sender, EventArgs e)
        {
            cancelAllTask();
        }

        private void btnCancelSellect_Click(object sender, EventArgs e)
        {
            cancelCurrentTask();
        }
    }
}