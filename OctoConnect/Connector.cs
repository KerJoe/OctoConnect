using RepetierHostExtender.basic;
using RepetierHostExtender.interfaces;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WebSocketSharp;

namespace OctoConnect
{
    public class Connector : PrinterConnectorBase, INotifyPropertyChanged, IDisposable, IPrintJob
    {
        IHost host;
        bool paused;
        bool connected;
        IRegMemoryFolder key;
        ConnectionPanel panel;
        WebSocket pushSocket;
        IPrintJob job;

        protected virtual void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, e);
        }

        string apikey;
        int port;
        string hostname;
        IPrinterConnection con;
        public string Apikey
        {
            get { return apikey; }
            set
            {
                apikey = value;
                OnPropertyChanged(new PropertyChangedEventArgs("Apikey"));
            }
        }
        public int Port
        {
            get { return port; }
            set
            {
                port = value;
                OnPropertyChanged(new PropertyChangedEventArgs("Port"));
            }
        }
        public string Hostname
        {
            get { return hostname; }
            set
            {
                hostname = value;
                OnPropertyChanged(new PropertyChangedEventArgs("Hostname"));
            }
        }

        public Connector(IHost _host)
        {
            host = _host;
            con = host.Connection;
        }

        private dynamic getJobJSON()
        {
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            string responseStr = wc.DownloadString(GetUri("job"));
            return Newtonsoft.Json.Linq.JObject.Parse(responseStr);            
        }

        public override string Name => "OctoPrint";

        public override string Id => "OctoConnect";

        //public override bool IsPaused => paused;
        public override bool IsPaused 
        { 
            get
            {
                var state = getJobJSON()["state"];
                return (state == "Pausing" || state == "Paused");
            } 
        }

        public override int MaxLayer => 0;// ? //throw new NotImplementedException();                

        public override IPrintJob Job => this;

        public override int InjectedCommands => 0;

        public static DateTime UnixTimeStampToDateTime(double unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }



        public float PercentDone => getJobJSON()["progress"]["completion"] * 100;
        public override string ETA
        {
            get
            {
                return getJobJSON()["job"]["estimatedPrintTime"];
            }
        }
        public override bool ETAModeNormal { get => true; set => throw new NotImplementedException(); }
        public int LinesSend => throw new NotImplementedException();//return getJobJSON()["progress"]["filepos"];
        public int TotalLines => throw new NotImplementedException();//return getJobJSON()["job"]["size"];
        public DateTime JobStarted
        {
            get
            {
                return UnixTimeStampToDateTime(getJobJSON()["job"]["date"]);
            }
        }
        public DateTime JobFinished => throw new NotImplementedException(); //UnixTimeStampToDateTime(getJobJSON()["job"]["date"] + getJobJSON()["job"]["estimatedPrintTime"]);
        public PrintJobMode Mode
        {
            get
            {// TODO: Check if correct
                switch(getJobJSON()["state"])
                {
                    case "Printing":
                    case "Pausing":
                    case "Paused":
                        return PrintJobMode.PRINTING;                        
                    case "Operational":
                        return PrintJobMode.FINISHED;
                    case "Error":
                    case "Cancelling":
                        return PrintJobMode.ABORTED;
                    case "Offline":
                    default:
                        return PrintJobMode.NO_JOB_DEFINED;                        
                }
            }
        }
        public string LastPrintingTime => throw new NotImplementedException(); //UnixTimeStampToDateTime(getJobJSON()["job"]["date"] + getJobJSON()["job"]["estimatedPrintTime"]);
        public bool Caching => false;
        public bool Exclusive => false;




        public double ComputedPrintingTime => throw new NotImplementedException();

        public event PropertyChangedEventHandler PropertyChanged;

        public override void Activate()
        {
            //throw new NotImplementedException();
            this.host.ShowHostComponent("OPConnect", false);
        }

        public override void AnalyzeResponse(string res)
        {
            //throw new NotImplementedException();
        }

        /*public string Headers
        {
            get
            {
                return string.Format(@"{
                    ""Content-Type"": ""application/json"",
                    ""X-Api-Key"": ""{0}""
                }", apikey);
            }            
        }*/
        public string GetUri(string part)
        {
            return string.Format(@"http://{0}:{1}/api/{2}?apikey={3}", hostname, port, part, apikey);
        }
        public string GetWS()
        {
            return string.Format(@"ws://{0}:{1}/sockjs/websocket", hostname, port);
        }

        public override bool Connect(bool failSilent = false)
        {
            // TODO: Uniform Request method (JSON vs ?x=y)
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";            
            try
            {
                string responseStr = wc.UploadString(GetUri("login"), @"{ ""passive"": true }"); // Log in to get username and session key
                dynamic response = Newtonsoft.Json.Linq.JObject.Parse(responseStr);

                string username = response["name"];
                string session = response["session"];

                if (pushSocket == null)
                {
                    pushSocket = new WebSocket(GetWS());
                    pushSocket.OnMessage += new EventHandler<MessageEventArgs>(pushSocket_OnMessage);
                    pushSocket.Connect();
                }            
                // TODO: This is stupid
                string auth_message = string.Format(@"( ""auth"": ""{0}:{1}"" )", username, session).Replace('(', '{').Replace(')', '}');
                pushSocket.Send(auth_message);

                // Connect Serial Printer
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                responseStr = wc.UploadString(GetUri("connection"), @"{ ""command"": ""connect"" }");

                connected = true;
                return true;
            }
            catch (Exception e)
            {
                host.LogError(e.ToString());                
            }
            return false;
        }

        private void pushSocket_OnMessage(object sender, MessageEventArgs e)
        {
            dynamic response = Newtonsoft.Json.Linq.JObject.Parse(e.Data);            
            try {
                var logs = response["current"]["logs"];
                foreach (string logline in logs)
                {
                    //host.LogInfo(logline);
                    if (logline.StartsWith("Recv: "))
                    {
                        var test = RepetierHostExtender.basic.LogLevel.DEFAULT;
                        host.LogInfo(logline.Remove(0, 6));
                        con.analyzeResponse(logline.Remove(0, 6), ref test);
                    }
                }
                
                //for (int i = 0; i < )                
            } catch (Exception) { }
        }

        public override UserControl ConnectionDialog()
        {
            if (panel == null)
            {
                panel = new ConnectionPanel(host);
                panel.Connect(this);
            }
            return panel;
        }

        public override void ContinueJob()
        {
            //throw new NotImplementedException();
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            string responseStr = wc.UploadString(GetUri("connection"), @"{ ""command"": ""pause"", ""action"": ""resume"" }");
        }

        public override void Deactivate()
        {
            //throw new NotImplementedException();
            this.host.HideHostComponent("OPConnect");
        }

        public override bool Disconnect(bool force)
        {
            //throw new NotImplementedException();
            return true;
        }

        public void Dispose()
        {
            //throw new NotImplementedException();
        }

        public override void Emergency()
        {
            //throw new NotImplementedException();
        }

        private ManualResetEvent injectLock = new ManualResetEvent(true);
        public override void GetInjectLock()
        {
            // ?
            //throw new NotImplementedException();
            try
            {
                injectLock.WaitOne();
                injectLock.Reset();
            }
            catch (Exception ex)
            {
                host.SetPrinterAction(ex.ToString());
            }
        }

        public override bool HasInjectedMCommand(int code)
        {
            // ? throw new NotImplementedException();
            return false;
        }

        public override void InjectManualCommand(string command)
        {
            if (!connected) return;
            //throw new NotImplementedException();
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            // TODO: This is stupid
            string command_message = string.Format(@"( ""command"": ""{0}"" )", command).Replace('(', '{').Replace(')', '}');
            wc.UploadString(GetUri("printer/command"), command_message);
        }

        public override void InjectManualCommandFirst(string command)
        {
            //throw new NotImplementedException();
            InjectManualCommand(command);
        }

        public override void InjectManualCommandReplace(string command)
        {
            //throw new NotImplementedException();
            InjectManualCommand(command);
        }

        public override bool IsConnected()
        {
            //throw new NotImplementedException();
            return connected;
        }

        public override bool IsJobRunning()
        {
            //throw new NotImplementedException();            
            return getJobJSON()["state"] != "Operational";
        }

        public override void KillJob()
        {
            //throw new NotImplementedException();
            // TODO: Test if paused
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            string responseStr = wc.UploadString(GetUri("connection"), @"{ ""command"": ""cancel"" }");            
        }

        public override void LoadFromRegistry()
        {
            // TODO: Check in begining
            Apikey = key.GetString("opapikey", apikey);
            Hostname = key.GetString("ophostname", hostname);
            Port = key.GetInt("opport", port);
        }

        public override void PauseJob(string text)
        {
            //throw new NotImplementedException();
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            string responseStr = wc.UploadString(GetUri("connection"), @"{ ""command"": ""pause"", ""action"": ""pause"" }");
        }

        public override void ResendLine(int line)
        {
            throw new NotImplementedException();
        }

        public override void ReturnInjectLock()        
        {
            // ?
            //throw new NotImplementedException();
            injectLock.Set();
        }

        public override void RunJob()
        {
            //throw new NotImplementedException();
            
            string preferredSaveName = host.GCodeEditor.PreferredSaveName;
            if (preferredSaveName == null)
            {
                return;
            }
            
            string filePath = System.IO.Path.GetTempPath() + preferredSaveName + ".gcode";
            StreamWriter file = File.CreateText(filePath);
            file.Write(host.GCodeEditor.getContent());
            file.Close();

            var wc = new WebClient();
            wc.UploadFile(GetUri("files/local"), filePath); // TODO: Send string Not temp file

            wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            wc.UploadString(GetUri(string.Format("files/local/{0}.gcode", preferredSaveName)), @"{ ""command"": ""select"", ""print"": ""true"" }"); // Select and start File

            // TODO: Test if paused and active
            //wc.UploadString(GetUri("job"), @"{ ""command"": ""start"" }");
        }

        public override void RunPeriodicalTasks()
        {
            //throw new NotImplementedException();
        }

        public override void SaveToRegistry()
        {
            key.SetString("opapikey", apikey == null ? "" : apikey);
            key.SetString("ophostname", hostname);
            key.SetInt("opport", port);
        }

        public override void SetConfiguration(IRegMemoryFolder _key)
        {
            key = _key;
        }

        public override void ToggleETAMode()
        {
            //throw new NotImplementedException();
        }

        public override void TrySendNextLine()
        {
            //throw new NotImplementedException();
        }
    }
}
