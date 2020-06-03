using MQTTnet;
using MQTTnet.Core;
using MQTTnet.Core.Adapter;
using MQTTnet.Core.Client;
using MQTTnet.Core.Diagnostics;
using MQTTnet.Core.Packets;
using MQTTnet.Core.Protocol;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace MqttNetServer
{
    class Program
    {
        private static string ApiAddress = ConfigurationManager.AppSettings.Get("ApiAddress");
        private static MqttClient mqttClient { get; set; }
        private static string topic = "gxsd";

        //缓存分割文件
        private static Dictionary<string, List<Base64FileInfo>> ListFile;
        static void Main(string[] args)
        {
            ListFile = new Dictionary<string, List<Base64FileInfo>>();

            MqttNetTrace.TraceMessagePublished += MqttNetTrace_TraceMessagePublished;
            Task.Run(async () => { await ConnectMqttServerAsync(); });

            while (true)
            {
                var inputString = Console.ReadLine().ToLower().Trim();
                var appMsg = new MqttApplicationMessage(topic, Encoding.UTF8.GetBytes(inputString), MqttQualityOfServiceLevel.ExactlyOnce, false);
                mqttClient.PublishAsync(appMsg);
            }
        }


        #region Mqtt
        private static async Task ConnectMqttServerAsync()
        {
            if (mqttClient == null)
            {
                mqttClient = new MqttClientFactory().CreateMqttClient() as MqttClient;
                mqttClient.ApplicationMessageReceived += MqttClient_ApplicationMessageReceived;
                mqttClient.Connected += MqttClient_Connected;
                mqttClient.Disconnected += MqttClient_Disconnected;
            }
            try
            {
                var options = new MqttClientTcpOptions
                {
                    Server = ConfigurationManager.AppSettings.Get("MqttServerAddress").Split(':').First(),
                    ClientId = Guid.NewGuid().ToString().Substring(0, 5),
                    UserName = "u001",
                    Password = "p001",
                    CleanSession = true,
                    Port = int.Parse(ConfigurationManager.AppSettings.Get("MqttServerAddress").Split(':').Last()),
                };

                await mqttClient.ConnectAsync(options);
                await mqttClient.SubscribeAsync(new List<TopicFilter> {
                    new TopicFilter(topic, MqttQualityOfServiceLevel.ExactlyOnce)
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("连接到MQTT服务器失败！" + Environment.NewLine + ex.Message + Environment.NewLine);
            }
        }
        private static void MqttClient_Connected(object sender, EventArgs e)
        {
            Console.WriteLine("已连接到MQTT服务器！");
        }
        private static void MqttClient_Disconnected(object sender, EventArgs e)
        {
            //重新连接
            Task.Run(async () => { await ConnectMqttServerAsync(); });
            Console.WriteLine("已断开MQTT连接！" + Environment.NewLine);
        }
        private static void MqttClient_ApplicationMessageReceived(object sender, MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                if (e.ApplicationMessage.Topic == "gxsd")
                {
                    var model = JsonConvert.DeserializeObject<RequestModel>(Encoding.UTF8.GetString(e.ApplicationMessage.Payload));
                    if (model != null && model.RequestId != null)
                    {
                        string resultInfo = "";
                        //文件类型
                        if (model.FileBase64Str != null && model.FileBase64Str.Count > 0)
                        {
                            foreach (var item in model.FileBase64Str)
                            {
                                //没有分割文件
                                if (string.IsNullOrEmpty(item.FileId))
                                {
                                    resultInfo = AffairUploadFile(item.FileBase64Str, item.FileName);
                                }
                                //分割的文件

                                else
                                {

                                    //还没有缓存
                                    if (!ListFile.Keys.Contains(item.FileId) || ListFile[item.FileId] == null)
                                    {
                                        ListFile.Add(item.FileId, new List<Base64FileInfo> { item });
                                    }
                                    else
                                    {
                                        //追加一个
                                        var list = ListFile[item.FileId];
                                        if (list.FirstOrDefault(o => o.FileIndex == item.FileIndex) == null)
                                        {
                                            list.Add(item);
                                            ListFile[item.FileId] = list;
                                        }
                                    }
                                    //最后一个，需要做组合
                                    if (item.IsLastIndex > 0)
                                    {
                                        //全部接受到了。
                                        if (item.IsLastIndex == ListFile[item.FileId].Count - 1)
                                        {
                                            //Base64文件组合
                                            var array = ListFile[item.FileId].OrderBy(o => o.FileIndex).Select(o => o.FileBase64Str).ToList();
                                            var base64Str = string.Join("", array);
                                            resultInfo = AffairUploadFile(base64Str, item.FileName);
                                            ListFile.Remove(item.FileId);
                                        }
                                        else
                                        {
                                            //循环读取缓存是否已请求到数据

                                        }
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (!string.IsNullOrEmpty(model.Url))
                            {
                                //model.Paramas = HttpUtility.UrlDecode(model.Paramas);
                                if (model.Methed.ToLower() == "get")
                                {
                                    resultInfo = RequestHelper.HttpGet(ApiAddress + model.Url, model.Paramas);
                                }
                                else
                                {
                                    resultInfo = RequestHelper.HttpPostByte(ApiAddress + model.Url, model.Paramas, model.ContentType);
                                }
                            }
                        }
                        var backModel = new BackModel { BackRequestId = model.RequestId, Result = resultInfo };
                        var fileMaxLeng = 869000;//一次文件最大限制   
                        var backStr = JsonConvert.SerializeObject(backModel);
                        if (backStr.Length < fileMaxLeng)
                        {
                            //Console.WriteLine($"接口返回：{resultInfo}");
                            var appMsg = new MqttApplicationMessage(topic, Encoding.UTF8.GetBytes(backStr), MqttQualityOfServiceLevel.ExactlyOnce, false);
                            mqttClient.PublishAsync(appMsg);
                        }




                        //文件过大，分多次发送
                        else
                        {
                            var fileArrary = SplitByLen(resultInfo, fileMaxLeng);
                            for (int j = 0; j < fileArrary.Count; j++)
                            {
                                var newResultStr = fileArrary[j];
                                backModel = new BackModel { BackRequestId = model.RequestId, Result = newResultStr, FileIndex = j };

                                if (j == fileArrary.Count - 1)
                                {
                                    backModel.IsLastIndex = j;
                                }
                                backStr = JsonConvert.SerializeObject(backModel);
                                var appMsg = new MqttApplicationMessage(topic, Encoding.UTF8.GetBytes(backStr), MqttQualityOfServiceLevel.ExactlyOnce, false);
                                mqttClient.PublishAsync(appMsg);
                            }
                        }

                    }
                }
            }
            catch (Exception exception)
            {

            }
        }
        #endregion

        private static void MqttNetTrace_TraceMessagePublished(object sender, MqttNetTraceMessagePublishedEventArgs e)
        {
            //Console.WriteLine($">> 线程ID：{e.ThreadId} 来源：{e.Source} 跟踪级别：{e.Level} 消息: {e.Message}");

            if (e.Exception != null)
            {
                Console.WriteLine(e.Exception);
            }
        }


        /// <summary>
        /// 上传附件
        /// </summary>
        /// <param name="file">文件流</param>
        /// <returns>地址</returns>
        public static string AffairUploadFile(string fileBase64Str, string fileName)
        {
            fileName = fileName.Replace(',', '-').Replace('/', '-').Replace('\\', '-');
            if (fileName.LastIndexOf('\\') > -1)
            {
                fileName = fileName.Substring(fileName.LastIndexOf('\\') + 1);
            }
            string requestUrl = ApiAddress + "/Tool/uploadFile?" + "fileName=" + fileName;
            HttpWebRequest request = WebRequest.Create(requestUrl) as HttpWebRequest;
            CookieContainer cookieContainer = new CookieContainer();
            request.CookieContainer = cookieContainer;
            request.AllowAutoRedirect = true;
            request.Method = "POST";
            string boundary = DateTime.Now.Ticks.ToString("X"); // 随机分隔线
            request.ContentType = "multipart/form-data;charset=utf-8;boundary=" + boundary;
            byte[] itemBoundaryBytes = Encoding.UTF8.GetBytes("\r\n--" + boundary + "\r\n");
            byte[] endBoundaryBytes = Encoding.UTF8.GetBytes("\r\n--" + boundary + "--\r\n");

            //请求头部信息 
            StringBuilder sbHeader = new StringBuilder(string.Format("Content-Disposition:form-data;name=\"file\";filename=\"{0}\"\r\nContent-Type:application/octet-stream\r\n\r\n", fileName));
            byte[] postHeaderBytes = Encoding.UTF8.GetBytes(sbHeader.ToString());

            //inputStream to arry
            byte[] bArr = Convert.FromBase64String(fileBase64Str);
            Stream postStream = request.GetRequestStream();
            postStream.Write(itemBoundaryBytes, 0, itemBoundaryBytes.Length);
            postStream.Write(postHeaderBytes, 0, postHeaderBytes.Length);
            postStream.Write(bArr, 0, bArr.Length);
            postStream.Write(endBoundaryBytes, 0, endBoundaryBytes.Length);
            postStream.Close();
            //发送请求并获取相应回应数据
            HttpWebResponse response = request.GetResponse() as HttpWebResponse;
            //直到request.GetResponse()程序才开始向目标网页发送Post请求
            Stream instream = response.GetResponseStream();
            StreamReader sr = new StreamReader(instream, Encoding.UTF8);
            //返回结果网页（html）代码
            string content = sr.ReadToEnd();
            return content;
        }

        /// <summary>
        /// 按字符串长度切分成数组
        /// </summary>
        /// <param name="str">原字符串</param>
        /// <param name="separatorCharNum">切分长度</param>
        /// <returns>字符串数组</returns>
        public static List<string> SplitByLen(string str, int separatorCharNum)
        {
            if (string.IsNullOrEmpty(str) || str.Length <= separatorCharNum)
            {
                return new List<string> { str };
            }
            string tempStr = str;
            List<string> strList = new List<string>();
            int iMax = Convert.ToInt32(Math.Ceiling(str.Length / (separatorCharNum * 1.0)));//获取循环次数
            for (int i = 1; i <= iMax; i++)
            {
                string currMsg = tempStr.Substring(0, tempStr.Length > separatorCharNum ? separatorCharNum : tempStr.Length);
                strList.Add(currMsg);
                if (tempStr.Length > separatorCharNum)
                {
                    tempStr = tempStr.Substring(separatorCharNum, tempStr.Length - separatorCharNum);
                }
            }
            return strList;
        }
    }
}