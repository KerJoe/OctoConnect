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
        public override int MaxLayer => throw new NotImplementedException();

        public double ComputedPrintingTime => throw new NotImplementedException();
        


        public override void Activate()
        {
            // ?
            this.host.ShowHostComponent("OPConnect", false);
        }
        public override void Deactivate()
        {
            // ?
            this.host.HideHostComponent("OPConnect"); 
        }



        public override void AnalyzeResponse(string res)
        {
            var test = RepetierHostExtender.basic.LogLevel.DEFAULT;
            con.analyzeResponse(res, ref test);
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
        string removeChecksumGCode(string gcodeStr)
        {
            if (gcodeStr.IndexOf('*') > 0)
                return gcodeStr.Substring(0, gcodeStr.IndexOf('*'));
            else
                return gcodeStr;
        }        
        private void pushSocket_OnMessage(object sender, MessageEventArgs e) // TODO: Buffer (Some log messages are skipped) ?
        {
            dynamic response = Newtonsoft.Json.Linq.JObject.Parse(e.Data);
            try {
                var logs = response["current"]["logs"];
                foreach (string logline in logs)
                {
                    host.LogInfo(logline);

                    if (logline.StartsWith("Send: "))
                    {
                        con.Analyzer.Analyze(new GCode(removeChecksumGCode(logline.Remove(0, 6))), false);
                        con.Analyzer.FireChanged();
                    }
                    if (logline.StartsWith("Recv: "))
                    {
                        var test = RepetierHostExtender.basic.LogLevel.DEFAULT;
                        con.analyzeResponse(logline.Remove(0, 6), ref test);
                    }
                }
            } catch (Exception) { }
        }



        public override void ToggleETAMode()
        {
            //throw new NotImplementedException();
        }

        public override void TrySendNextLine()
        {
            //throw new NotImplementedException();
        }

        public override void Emergency()
        {
            InjectManualCommandFirst("M112");
        }        



        public override void InjectManualCommand(string command)
        {
            if (!isConnected) return;
            //throw new NotImplementedException();
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            // TODO: This is stupid
            string command_message = string.Format(@"( ""command"": ""{0}"" )", command).Replace('(', '{').Replace(')', '}');
            wc.UploadString(GetUri("printer/command"), command_message); // TODO: Exception handling            
        }
        public override void InjectManualCommandFirst(string command)
        {
            InjectManualCommand(command);
        }
        public override void InjectManualCommandReplace(string command)
        {
            // ?
            InjectManualCommand(command);
        }
        public override void ResendLine(int line)
        {
            throw new NotImplementedException();
        }
        public override bool HasInjectedMCommand(int code)
        {
            // ? throw new NotImplementedException();
            return false;
        }



        private ManualResetEvent injectLock = new ManualResetEvent(true);
        public override void GetInjectLock()
        {
            // ?            
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
        public override void ReturnInjectLock()
        {
            // ?
            injectLock.Set();
        }









        IHost host;
        IPrinterConnection con;
        IRegMemoryFolder key;
        ConnectionPanel panel;
        WebSocket pushSocket;
        dynamic jobJson;
        public Connector(IHost _host)
        {
            host = _host;
            con = host.Connection;
        }
        public void Dispose()
        {
            //throw new NotImplementedException();
        }



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
                }
                pushSocket.Connect(); // TODO ????
                // TODO: This is stupid               
                string auth_message = string.Format(@"( ""auth"": ""{0}:{1}"" )", username, session).Replace('(', '{').Replace(')', '}');
                pushSocket.Send(auth_message);

                // Connect Serial Printer
                wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                responseStr = wc.UploadString(GetUri("connection"), @"{ ""command"": ""connect"" }");

                isConnected = true;
                host.UpdateJobButtons();
                con.FireConnectionChange("Connected"); // Only required only in connect, not disconnect (for some reason)
                                                       //con.Analyzer.FireChanged();// ?
                
                host.ClearPrintPreview();                
                return true;
            }
            catch (Exception e)
            {
                isConnected = false; 
                host.LogError(e.ToString());
                return false;
            }
        }
        public override bool Disconnect(bool force)
        {
            // TODO: Complete
            if (pushSocket != null) pushSocket.Close();
            isConnected = false;
            return true;
        }



        async Task UploadLocalFileAsync(string filepath, string filename)
        {
            var wc = new WebClient();
            var response = await wc.UploadFileTaskAsync(GetUri("files/local"), filepath);

            // TODO: Test if paused and active
            wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            wc.UploadString(GetUri(string.Format("files/local/{0}.gcode", filename)), @"{ ""command"": ""select"", ""print"": ""true"" }"); // Select and start File
        }
        public override void RunJob()
        {
            string filename = host.GCodeEditor.PreferredSaveName;
            if (filename == null) return;

            // TODO: Send string Not temp file
            string filepath = System.IO.Path.GetTempPath() + filename + ".gcode";
            StreamWriter file = File.CreateText(filepath);
            file.Write(host.GCodeEditor.getContent());
            file.Close();

            Task.Run(() => UploadLocalFileAsync(filepath, filename));

            con.Analyzer.drawing = true;
            con.Analyzer.FireChanged();
            host.UpdateJobButtons();
        }
        public override void KillJob()
        {
            // TODO: Test if paused
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            string responseStr = wc.UploadString(GetUri("job"), @"{ ""command"": ""cancel"" }");

            host.UpdateJobButtons();
        }
        public override void PauseJob(string text)
        {
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            string responseStr = wc.UploadString(GetUri("job"), @"{ ""command"": ""pause"", ""action"": ""pause"" }");

            isJobRunning = true;
            isPaused = true;
            host.UpdateJobButtons();

            MessageBox.Show(text, "Pause", MessageBoxButtons.OK);

            host.FireNamedEvent("core:printjobBeforeContinue", null);
            con.connector.ContinueJob();
            host.FireNamedEvent("core:printjobAfterContinue", null);
        }
        public override void ContinueJob()
        {
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            string responseStr = wc.UploadString(GetUri("job"), @"{ ""command"": ""pause"", ""action"": ""resume"" }");

            host.UpdateJobButtons();
        }



        int periodicalCounter;
        const int jobUpdateDivider = 10;
        // If type is null convert to safe value for this type
        dynamic ifNullThenZero(dynamic data)
        {
            if (data == null) return 0;
            else return data;
        }
        dynamic ifNullThenEmpty(dynamic data)
        {
            if (data == null) return "";
            else return data;
        }
        private dynamic getJobJSON()
        {
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            string responseStr = wc.DownloadString(GetUri("job"));
            return Newtonsoft.Json.Linq.JObject.Parse(responseStr);
        }
        public static DateTime unixTimeStampToDateTime(double unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            System.DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, System.DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }
        public override void RunPeriodicalTasks()
        {
            periodicalCounter++;
            if (periodicalCounter >= 1000000)
                periodicalCounter = 0;

            if (isConnected)
                if (periodicalCounter % jobUpdateDivider == 0)
                {
                    host.UpdateJobButtons();
                    // TODO: Put into seperate thread
                    jobJson = getJobJSON();

                    //isJobRunning = (jobJson["state"] != "Operational");
                    isJobRunning = (jobJson["state"] != "Operational");
                    isPaused = (jobJson["state"] == "Pausing" || jobJson["state"] == "Paused");

                    percentDone = ifNullThenZero(jobJson["progress"]["completion"]);                    
                    eta = DateTime.MinValue.AddSeconds((double)ifNullThenZero(jobJson["progress"]["printTimeLeft"])).ToString("T-%H:%MM:%s"); // TODO: Internationalize     
                    jobStarted = unixTimeStampToDateTime((double)ifNullThenZero(jobJson["job"]["file"]["date"]));
                    double a = ifNullThenZero(jobJson["job"]["file"]["date"]);
                    double b = ifNullThenZero(jobJson["job"]["estimatedPrintTime"]);
                    double c = ifNullThenZero(jobJson["progress"]["printTime"]);
                    double d = ifNullThenZero(jobJson["progress"]["printTimeLeft"]);
                    jobFinished = unixTimeStampToDateTime((double)(a + c + d)); // TODO: Another way not using calculation
                    lastPrintingTime = jobFinished.ToString();

                    switch ((string)jobJson["state"]) // TODO: Check if correct
                    {
                        case "Printing":
                        case "Pausing":
                        case "Paused":
                            mode = PrintJobMode.PRINTING; break;
                        case "Error":
                        case "Cancelling":
                            mode = PrintJobMode.ABORTED; break;
                        case "Offline":
                        case "Operational":                        
                            if (ifNullThenZero(jobJson["progress"]["completion"]) == 100)
                                mode = PrintJobMode.FINISHED;
                            else
                                mode = PrintJobMode.NO_JOB_DEFINED; 
                            break;
                        default:
                            mode = PrintJobMode.NO_JOB_DEFINED; break;
                    }
                    // TODO: Find actual lines not bytes
                    int bytesSendJob = (int)ifNullThenZero(jobJson["progress"]["filepos"]);
                    int totalBytesJob = (int)ifNullThenZero(jobJson["job"]["file"]["size"]);
                    //linesSendJob  = (int)ifNullThenZero(jobJson["progress"]["filepos"]); 
                    //totalLinesJob = (int)ifNullThenZero(jobJson["job"]["file"]["size"]);
                    linesSendJob = 10;
                    totalLinesJob = 20;
                    /*jobGcode = host.GCodeEditor.getContent();
                    for (int i = 0; i < jobGcode.Length; i++)
                    {
                        if (jobGcode[i] == '\n')
                        {
                            if (i < bytesSendJob) linesSendJob++; // TODO: Test for 'off by one error'
                            totalLinesJob++;
                        }
                    }*/
                    con.Analyzer.hasXHome = true;
                    con.Analyzer.hasYHome = true;
                    con.Analyzer.hasZHome = true;
                    con.Analyzer.FireChanged();
                }
        }



        public override void SetConfiguration(IRegMemoryFolder _key)
        {
            key = _key;
        }
        public override void LoadFromRegistry()
        {
            // TODO: Check in begining
            Apikey = key.GetString("opapikey", apikey);
            Hostname = key.GetString("ophostname", hostname);
            Port = key.GetInt("opport", port);
        }
        public override void SaveToRegistry()
        {
            key.SetString("opapikey", apikey == null ? "" : apikey);
            key.SetString("ophostname", hostname);
            key.SetInt("opport", port);
        }



        public override string Name => "OctoPrint";
        public override string Id => "OctoConnect";
        public override IPrintJob Job => this;
        public override int InjectedCommands => 0;
        bool isConnected = false;   public override bool IsConnected() => isConnected;
        bool isJobRunning = false;  public override bool IsJobRunning() => isJobRunning;
        bool isPaused = false;      public override bool IsPaused => isPaused;
        //public override bool ServerConnection => true; // TODO: Decide to use or not to use server connection type



        string jobGcode = "";
        // IPrintJob interface implementation
        float percentDone;          public float PercentDone => percentDone;
        int linesSendJob;           public int LinesSend => linesSendJob;
        int totalLinesJob;          public int TotalLines => totalLinesJob;
        string eta;                 public override string ETA => eta;
        DateTime jobStarted;        public DateTime JobStarted => jobStarted;
        DateTime jobFinished;       public DateTime JobFinished => jobFinished;
        PrintJobMode mode;          public PrintJobMode Mode => mode;
        string lastPrintingTime;    public string LastPrintingTime => lastPrintingTime;        
        public bool Caching => false;
        public bool Exclusive => false;
        public override bool ETAModeNormal { get => true; set => throw new NotImplementedException(); } // TODO: ?    



        public override UserControl ConnectionDialog()
        {
            if (panel == null)
            {
                panel = new ConnectionPanel(host);
                panel.Connect(this);
            }
            return panel;
        }



        // Get event on changes in connector settings panel
        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            if (PropertyChanged != null)
                PropertyChanged(this, e);
        }
        string apikey; public string Apikey
        {
            get { return apikey; }
            set { apikey = value; OnPropertyChanged(new PropertyChangedEventArgs("Apikey")); }
        }
        int port; public int Port
        {
            get { return port; }
            set { port = value; OnPropertyChanged(new PropertyChangedEventArgs("Port")); }
        }
        string hostname; public string Hostname
        {
            get { return hostname; }
            set { hostname = value; OnPropertyChanged(new PropertyChangedEventArgs("Hostname")); }
        }
    }
}
