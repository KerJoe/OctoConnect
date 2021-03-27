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
        IPrinterConnection con;
        IRegMemoryFolder key;
        ConnectionPanel panel;
        WebSocket pushSocket;
        dynamic jobJson;



        public override string Name => "OctoPrint";
        public override string Id => "OctoConnect";
        public override IPrintJob Job => this;
        public override bool HasInjectedMCommand(int code) => false;
        public override int InjectedCommands => 0;
        public override int MaxLayer => throw new NotImplementedException(); // ?
        public double ComputedPrintingTime => throw new NotImplementedException(); // ?
        bool isConnected = false; public override bool IsConnected() => isConnected;
        bool isJobRunning = false; public override bool IsJobRunning() => isJobRunning;
        bool isPaused = false; public override bool IsPaused => isPaused;
        //public override bool ServerConnection => true; // TODO: Decide to use or not to use server connection type



        // IPrintJob interface implementation
        float percentDone; public float PercentDone => percentDone;
        int linesSendJob; public int LinesSend => linesSendJob;
        int totalLinesJob; public int TotalLines => totalLinesJob;
        string eta; public override string ETA => eta;
        DateTime jobStarted; public DateTime JobStarted => jobStarted;
        DateTime jobFinished; public DateTime JobFinished => jobFinished;
        PrintJobMode mode; public PrintJobMode Mode => mode;
        string lastPrintingTime; public string LastPrintingTime => lastPrintingTime;
        public bool Caching => false;
        public bool Exclusive => false;
        public override bool ETAModeNormal { get => true; set => throw new NotImplementedException(); } // ?    



        public Connector(IHost _host)
        {
            host = _host;
            con  = host.Connection;
        }
        ~Connector()
        {
            Dispose();
        }
        public void Dispose()
        {
            if (panel != null)
            {
                panel.Dispose();
            }
            if (pushSocket != null)
            {
                if (isConnected)
                {
                    pushSocket.Close();
                }
                pushSocket = null;
                isConnected = false;
            }
            if (injectLock != null)
            {
                injectLock.Dispose();
            }
        }



        public override void Activate() { }
        public override void Deactivate() { }



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
            string responseStr = "";
            try
            {
                responseStr = wc.UploadString(GetUri("login"), @"{ ""passive"": true }"); // Log in to get username and session key
            }
            catch (WebException e)
            {
                isConnected = false;
                if (e.Response == null)
                    MessageBox.Show("Unable to connect to remote server", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    switch (((HttpWebResponse)e.Response).StatusCode)
                    {
                        case HttpStatusCode.Forbidden:
                            MessageBox.Show("Connection Failed: Incorrect api key", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);                        
                            break;
                        default:
                            MessageBox.Show(e.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            break;
                    }                
                return false;
            }
            dynamic response = Newtonsoft.Json.Linq.JObject.Parse(responseStr);

            string username = response["name"];
            string session = response["session"];

            if (pushSocket == null)
            {
                pushSocket = new WebSocket(GetWS());
                pushSocket.OnMessage += new EventHandler<MessageEventArgs>(pushSocket_OnMessage);
                pushSocket.OnError   += new EventHandler<WebSocketSharp.ErrorEventArgs>(pushSocket_OnError);
            }
            pushSocket.Connect();            
            string auth_message = string.Format(@"{{ ""auth"": ""{0}:{1}"" }}", username, session);
            pushSocket.Send(auth_message); // TODO: Test if successfully connected ?

            // Connect Serial Printer
            wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            responseStr = wc.UploadString(GetUri("connection"), @"{ ""command"": ""connect"" }");

            isConnected = true;
            host.UpdateJobButtons();
            con.FireConnectionChange("Connected"); // Only required in Connect, not Disconnect
            host.ClearPrintPreview();
            return true;
        }
        public override bool Disconnect(bool force)
        {
            isConnected = false;
            if (pushSocket != null) pushSocket.Close();            
            return true;
        }



        public override void AnalyzeResponse(string res)
        {
            var test = RepetierHostExtender.basic.LogLevel.DEFAULT;
            con.analyzeResponse(res, ref test);
        }
        public override void Emergency()
        {
            InjectManualCommandFirst("M112");
        }
        public override void ToggleETAMode()
        {
            throw new NotImplementedException();
        }



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
            try
            {
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
            }
            catch (Exception) { }
        }
        private void pushSocket_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            if (!isConnected) return;
            MessageBox.Show(e.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            string responseStr;
            try
            {
                responseStr = wc.DownloadString(GetUri("job"));
            }
            catch (WebException e)
            {
                Disconnect(true);
                con.FireConnectionChange("Disconnected");
                if (e.Response == null)
                    MessageBox.Show("Unable to connect to remote server", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);                                
                return null;
            }
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
                    // TODO: Put into seperate thread
                    if ((jobJson = getJobJSON()) != null)
                    {
                        percentDone = ifNullThenZero(jobJson["progress"]["completion"]);
                        eta = DateTime.MinValue.AddSeconds((double)ifNullThenZero(jobJson["progress"]["printTimeLeft"])).ToString("T-%HH:%m:%s"); // TODO: Internationalize     
                        jobStarted = unixTimeStampToDateTime((double)ifNullThenZero(jobJson["job"]["file"]["date"]));
                        double a = ifNullThenZero(jobJson["job"]["file"]["date"]);
                        double b = ifNullThenZero(jobJson["job"]["estimatedPrintTime"]);
                        double c = ifNullThenZero(jobJson["progress"]["printTime"]);
                        double d = ifNullThenZero(jobJson["progress"]["printTimeLeft"]);
                        jobFinished = unixTimeStampToDateTime((double)(a + c + d)); // TODO: Another way not using calculation
                        lastPrintingTime = jobFinished.ToString();

                        isJobRunning = ((jobJson["state"] != "Operational") && (jobJson["state"] != "Offline"));
                        isPaused = (jobJson["state"] == "Pausing" || jobJson["state"] == "Paused");
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
                        host.UpdateJobButtons(); // TODO: May be a potential slowdown, do conditionaly ?

                        int bytesSendJob = (int)ifNullThenZero(jobJson["progress"]["filepos"]);
                        int totalBytesJob = (int)ifNullThenZero(jobJson["job"]["file"]["size"]);
                        // TODO: Find actual lines not bytes
                        linesSendJob = bytesSendJob; 
                        totalLinesJob = totalBytesJob;

                        host.SetPrinterAction(string.Format("Done: {0}%", percentDone.ToString("##0.00")));
                        host.SendProgress(ProgressType.PRINT_JOB, percentDone);                        
                    }
                }
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

            // TODO: Send string Not temp file ?
            string filepath = System.IO.Path.GetTempPath() + filename + ".gcode";
            StreamWriter file = File.CreateText(filepath);
            file.Write(host.GCodeEditor.getContent());
            file.Close();

            Task.Run(() => UploadLocalFileAsync(filepath, filename));

            host.ClearPrintPreview();
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



        public override void InjectManualCommand(string command)
        {
            if (!isConnected) return;
            
            var wc = new WebClient(); wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            string command_message = string.Format(@"{{ ""command"": ""{0}"" }}", command);
 
            try
            {
                wc.UploadString(GetUri("printer/command"), command_message);
            }
            catch (WebException e)
            {
                Disconnect(true);
                con.FireConnectionChange("Disconnected");
                if (e.Response == null)
                    MessageBox.Show("Unable to connect to remote server", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    switch (((HttpWebResponse)e.Response).StatusCode)
                    {
                        case HttpStatusCode.Conflict:
                            MessageBox.Show("Send command failed: No printer connected to OctoPrint", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);                                                        
                            break;
                        default:
                            MessageBox.Show(e.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            break;
                    }
            }
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
        public override void TrySendNextLine()
        {
            throw new NotImplementedException();
        }
        public override void ResendLine(int line)
        {
            throw new NotImplementedException();
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



        public override void SetConfiguration(IRegMemoryFolder _key)
        {
            key = _key;
        }
        public override void LoadFromRegistry()
        {
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
        string apikey = ""; public string Apikey
        {
            get { return apikey; }
            set { apikey = value; OnPropertyChanged(new PropertyChangedEventArgs("Apikey")); }
        }
        int port = 5000; public int Port
        {
            get { return port; }
            set { port = value; OnPropertyChanged(new PropertyChangedEventArgs("Port")); }
        }
        string hostname = "localhost"; public string Hostname
        {
            get { return hostname; }
            set { hostname = value; OnPropertyChanged(new PropertyChangedEventArgs("Hostname")); }
        }
    }
}
