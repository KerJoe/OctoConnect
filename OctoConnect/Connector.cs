using RepetierHostExtender.basic;
using RepetierHostExtender.interfaces;
using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using WebSocketSharp;
using Newtonsoft.Json.Linq;
using System.Text;

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
        public override int MaxLayer => 9999; // ?        
        bool isConnected = false; public override bool IsConnected() => isConnected;
        bool isJobRunning = false; public override bool IsJobRunning() => isJobRunning && isConnected;
        bool isPaused = false; public override bool IsPaused => isPaused;
        //public override bool ServerConnection => true; // TODO: Decide to use or not to use server connection type


        
        // Default mode has a "true" value
        const bool ETEMode = true;  // Estimated Time En route   (Time left till job is done)
        const bool ETAMode = false; // Estimated Time of Arrival (Time when job will be done)        
        bool etMode = ETEMode; // ETE is a default mode



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
        public bool Exclusive => false; // ?
        public override bool ETAModeNormal { get => etMode; set => etMode = value; }
        public double ComputedPrintingTime => throw new NotImplementedException(); // ?



        public Connector(IHost _host)
        {
            host = _host;
            con = host.Connection;
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
            }
            if (injectLock != null)
            {
                injectLock.Dispose();
            }
            isConnected = false;
        }



        public override void Activate() { }
        public override void Deactivate() { }



        dynamic getOctoPrintJSON(string apiname)
        {
            var wc = new WebClient();
            wc.Headers["X-Api-Key"] = apikey; wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            string response = wc.DownloadString(string.Format(@"{0}://{1}:{2}/api/{3}?apikey={4}", usessl ? "https" : "http", hostname, port, apiname, apikey));
            return JObject.Parse(response == "" ? "{}" : response); // Parse method doesn't accept an empty string, so instead we give it an empty JSON
        }
        dynamic postOctoPrintJSON(string apiname, string json = "")
        {
            var wc = new WebClient();
            wc.Headers["X-Api-Key"] = apikey; wc.Headers[HttpRequestHeader.ContentType] = "application/json";
            string response = wc.UploadString(string.Format(@"{0}://{1}:{2}/api/{3}", usessl ? "https" : "http", hostname, port, apiname), json);
            return JObject.Parse(response == "" ? "{}" : response);
        }
        float uploadPercentDone = 0;
        bool uploadDone = false;        
        Task<byte[]> postOctoPrintGCode(string filepath)
        {
            var wc = new WebClient(); uploadPercentDone = 0; uploadDone = false;
            wc.UploadProgressChanged += (o, e) => uploadPercentDone = ((float)e.BytesSent / e.TotalBytesToSend) * 100; // ProgressPercentage is int, not double
            wc.UploadFileCompleted += (o, e) => uploadDone = true;
            wc.Headers["X-Api-Key"] = apikey;
            return wc.UploadFileTaskAsync(new Uri(string.Format(@"{0}://{1}:{2}/api/files/local", usessl ? "https" : "http", hostname, port)), filepath);
        }
        bool connectToPrinter()
        {
            // Connect OctoPrint to printer
            dynamic jobResponse;             
            int timeoutMiliseconds = 10000;
            try
            {
                postOctoPrintJSON("connection", @"{ ""command"": ""connect"" }");
                for (; timeoutMiliseconds != 0; timeoutMiliseconds -= 100)
                {
                    jobResponse = getOctoPrintJSON("job");
                    string printerState = jobResponse["state"];
                    if (printerState.Contains("Offline"))
                    {
                        MessageBox.Show("Unable to connect to printer:\n" + printerState, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return false;
                    }
                    if (printerState != "Detecting serial connection")
                        break;
                    Thread.Sleep(100);
                }
                if (timeoutMiliseconds == 0)
                {
                    MessageBox.Show("Unable to connect to printer:\nTimeout", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
            }
            catch (WebException ex)
            {
                Disconnect(false);
                if (ex.Response == null)
                    MessageBox.Show("Unable to connect to remote server", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);                            
                return false;
            }
            return true;
        }
        public override bool Connect(bool failSilent = false)
        {
            dynamic loginResponse;
            try
            {
                loginResponse = postOctoPrintJSON("login", @"{ ""passive"": true }"); // Log in to get username and session key            
            }
            catch(WebException ex)
            {
                Disconnect(false);
                if (ex.Response == null)
                    MessageBox.Show("Unable to connect to remote server", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    switch (((HttpWebResponse)ex.Response).StatusCode)
                    {
                        case HttpStatusCode.Forbidden:
                            MessageBox.Show("Connection Failed: Incorrect api key", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            break;
                        default:
                            MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            break;
                    }
                return false;
            }

            string username = loginResponse["name"];
            string session = loginResponse["session"];

            string websocketUri = string.Format(@"{0}://{1}:{2}/sockjs/websocket", usessl ? "wss" : "ws", hostname, port);
            if (pushSocket != null)
                if (pushSocket.Url.ToString() != websocketUri) // Test if address in panel changed            
                {
                    pushSocket.Close();
                    pushSocket = null;
                }
            if (pushSocket == null)
            {
                pushSocket = new WebSocket(websocketUri);
                pushSocket.OnMessage += new EventHandler<MessageEventArgs>(pushSocket_OnMessage);
                pushSocket.OnError   += new EventHandler<WebSocketSharp.ErrorEventArgs>(pushSocket_OnError);
            }
            pushSocket.Connect();            
            string authMessage = string.Format(@"{{ ""auth"": ""{0}:{1}"" }}", username, session);
            pushSocket.Send(authMessage); // TODO: Test if successfully connected ?

            if (!connectToPrinter()) return false;

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
            var logLevel = RepetierHostExtender.basic.LogLevel.DEFAULT;
            con.analyzeResponse(res, ref logLevel);
        }
        public override void Emergency()
        {
            InjectManualCommandFirst("M112");
            isConnected = false;
            con.FireConnectionChange("Disconnected"); // TODO: Reconnect after stop ?
        }
        public override void ToggleETAMode() // Toggle mode when pressing on printer action (label near progress bar)
        {
            etMode = !etMode;
        }



        string removeChecksumGCode(string gcodeStr)
        {
            if (gcodeStr.IndexOf('*') > 0)
                return gcodeStr.Substring(0, gcodeStr.IndexOf('*'));
            else
                return gcodeStr;
        }
        private void pushSocket_OnMessage(object sender, MessageEventArgs e) // TODO: Buffer (Some log messages apear to be skipped) ? 
        {
            var response = JObject.Parse(e.Data);
            if (response.SelectToken("current.logs") != null)
            {
                dynamic logs = response["current"]["logs"];
                foreach (string logline in logs)
                {
                    host.LogInfo(logline);

                    if (logline.StartsWith("Send: "))
                    {
                        con.Analyzer.Analyze(new GCode(removeChecksumGCode(logline.Remove(0, 6))), false);
                    }
                    if (logline.StartsWith("Recv: "))
                    {
                        AnalyzeResponse(logline.Remove(0, 6));
                    }
                }
            }
        }
        private void pushSocket_OnError(object sender, WebSocketSharp.ErrorEventArgs e)
        {
            if (!isConnected) return;
            MessageBox.Show(e.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }



        public static DateTime unixTimeStampToDateTime(double unixTimeStamp)
        {
            // Unix timestamp is seconds past epoch
            DateTime dtDateTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
            dtDateTime = dtDateTime.AddSeconds(unixTimeStamp).ToLocalTime();
            return dtDateTime;
        }
        async void updateJobAsync()
        {
            if ((jobJson = getOctoPrintJSON("job")) != null)
            {
                jobStarted = unixTimeStampToDateTime((double?)jobJson["job"]["file"]["date"] ?? 0); // Explicit (float?) required because null coelesence doens't recognize JValue being null
                jobFinished = unixTimeStampToDateTime((double?)jobJson["job"]["file"]["date"] ?? 0 + (double?)jobJson["progress"]["printTime"] ?? 0);
                lastPrintingTime = jobFinished.ToString();
                if (etMode == ETEMode)
                {
                    // TODO: Addaptive ETE length (If hours equal 0 then don't display them) ?
                    TimeSpan eteTimeSpan = TimeSpan.FromSeconds((double?)jobJson["progress"]["printTimeLeft"] ?? 0);

                    string eteHours = ((int)(eteTimeSpan.TotalHours)).ToString("00"); // Convert to int to floor value
                    string eteMins = eteTimeSpan.Minutes.ToString("00");
                    string eteSecs = eteTimeSpan.Seconds.ToString("00");
                    eta = string.Format("{0}:{1}:{2}", eteHours, eteMins, eteSecs);
                }
                else // ETAMode
                {
                    DateTime etaDateTime = DateTime.Now.AddSeconds((double?)jobJson["progress"]["printTimeLeft"] ?? 0);

                    eta = etaDateTime.ToLongTimeString();
                }

                isJobRunning = ((jobJson["state"] != "Operational") && (jobJson["state"] != "Offline"));
                isPaused = (jobJson["state"] == "Pausing" || jobJson["state"] == "Paused");

                if (!con.Analyzer.uploading)
                    percentDone = (float?)jobJson["progress"]["completion"] ?? 0;
                else
                {
                    percentDone = uploadPercentDone; // TODO: Potentially unsafe  ?
                    isJobRunning = true; 
                }
                
                switch ((string)jobJson["state"])
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
                        if ((bool?)jobJson["progress"]["completion"] ?? 0 == 100)
                            mode = PrintJobMode.FINISHED;
                        else
                            mode = PrintJobMode.NO_JOB_DEFINED;
                        break;
                    default:
                        mode = PrintJobMode.NO_JOB_DEFINED; break;
                }
                host.UpdateJobButtons(); // TODO: May be a potential slowdown, do conditionaly ?

                int bytesSendJob = (int?)jobJson["progress"]["filepos"] ?? 0;
                int totalBytesJob = (int?)jobJson["job"]["file"]["size"] ?? 0;
                // TODO: Find actual lines not bytes
                linesSendJob = bytesSendJob;
                totalLinesJob = totalBytesJob;

                host.SetPrinterAction(string.Format("Done: {0}%", percentDone.ToString("##0.00")));
                host.SendProgress(ProgressType.PRINT_JOB, percentDone);

                if (isJobRunning) // If we are in a job assume we are homed
                {
                    con.Analyzer.hasXHome = true;
                    con.Analyzer.hasYHome = true;
                    con.Analyzer.hasZHome = true;
                }
            }
        }
        Task updateJobAsyncTask;
        public override void RunPeriodicalTasks()
        {
            if (isConnected)
            {
                if (updateJobAsyncTask == null)
                    updateJobAsyncTask = Task.Run(() => updateJobAsync());
                if (updateJobAsyncTask.Status == TaskStatus.RanToCompletion)
                {
                    updateJobAsyncTask = Task.Run(() => updateJobAsync());
                }
            }
        }



        public override void RunJob()
        {
            string filename = host.GCodeEditor.PreferredSaveName;
            if (filename == null || filename == "") return;
                        
            Task.Run(() =>
            {
                // TODO: Send string, NOT temp file ?
                string filepath = Path.GetTempPath() + filename + ".gcode";
                StreamWriter file = File.CreateText(filepath);
                file.Write(host.GCodeEditor.getContent());
                file.Close();

                con.Analyzer.uploading = true; // Allow display of uploading percentage
                host.UpdateJobButtons();
                try
                {
                    postOctoPrintGCode(filepath).Wait(); // Upload file
                    con.Analyzer.uploading = false;
                    postOctoPrintJSON(string.Format("files/local/{0}.gcode", filename), @"{ ""command"": ""select"", ""print"": ""true"" }"); // Select and start File
                }
                catch(WebException ex)
                {
                    Disconnect(false);
                    con.Analyzer.uploading = false;
                    if (ex.Response == null)
                        MessageBox.Show("Unable to connect to remote server", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    else
                        switch (((HttpWebResponse)ex.Response).StatusCode)
                        {
                            case HttpStatusCode.Conflict:
                                MessageBox.Show("Upload Failed: File is in use", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                break;
                            default:
                                MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                break;
                        }
                    return;
                }

                con.Analyzer.uploading = false;
                host.ClearPrintPreview();
                host.UpdateJobButtons();
            });            
        }
        public override void KillJob()
        {
            if (con.Analyzer.uploading) return; // TODO: Kill uploading instead

            postOctoPrintJSON("job", @"{ ""command"": ""cancel"" }");

            host.UpdateJobButtons();
        }
        public override void PauseJob(string text)
        {
            if (con.Analyzer.uploading) return;

            try
            {
                postOctoPrintJSON("job", @"{ ""command"": ""pause"", ""action"": ""pause"" }");                
            }
            catch (WebException ex)
            {
                Disconnect(true); con.FireConnectionChange("Disconnected");
                if (ex.Response == null)
                    MessageBox.Show("Unable to connect to remote server", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }


            isJobRunning = true;
            isPaused = true;
            host.UpdateJobButtons();

            Task.Run(() =>
            {
                // Display message box on top, like what the default repetier-host behaviour is
                MessageBox.Show(new Form { TopMost = true }, text, "Pause", MessageBoxButtons.OK);
                if (isJobRunning == false) return; // If the job was canceled while we were waiting for message box, don't resume it

                host.FireNamedEvent("core:printjobBeforeContinue", null); // TODO: Test if necessary
                con.connector.ContinueJob();
                host.FireNamedEvent("core:printjobAfterContinue", null);
            });
        }
        public override void ContinueJob()
        {            
            try
            {
                postOctoPrintJSON("job", @"{ ""command"": ""pause"", ""action"": ""resume"" }");
            }
            catch (WebException ex)
            {
                Disconnect(true); con.FireConnectionChange("Disconnected");
                if (ex.Response == null)
                    MessageBox.Show("Unable to connect to remote server", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            host.UpdateJobButtons();
        }



        public override void InjectManualCommand(string command)
        {
            if (!isConnected) return;
            try
            {
                postOctoPrintJSON("printer/command", string.Format(@"{{ ""command"": ""{0}"" }}", command));
            }
            catch (WebException ex)
            {
                Disconnect(true); con.FireConnectionChange("Disconnected");
                if (ex.Response == null)
                    MessageBox.Show("Unable to connect to remote server", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else
                    switch (((HttpWebResponse)ex.Response).StatusCode)
                    {
                        case HttpStatusCode.Conflict:
                            MessageBox.Show("Communication Fail: No printer connected to OctoPrint", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);                                                        
                            break;
                        default:
                            MessageBox.Show(ex.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            break;
                    }
            }
        }
        public override void InjectManualCommandFirst(string command) // ?
        {            
            InjectManualCommand(command);
        }
        public override void InjectManualCommandReplace(string command) // ?
        {            
            InjectManualCommand(command);
        }
        public override void TrySendNextLine() { } // ?        
        public override void ResendLine(int line) { } // ?



        private ManualResetEvent injectLock = new ManualResetEvent(true); // ?
        public override void GetInjectLock() // ?
        {                        
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
        public override void ReturnInjectLock() // ?
        {
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
            UseSsl = key.GetBool("opusessl", usessl);
        }
        public override void SaveToRegistry()
        {
            key.SetString("opapikey", apikey == null ? "" : apikey);
            key.SetString("ophostname", hostname);
            key.SetInt("opport", port);
            key.SetBool("opusessl", usessl);
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

        bool usessl = false; public bool UseSsl
        {
            get { return usessl; }
            set { usessl = value; OnPropertyChanged(new PropertyChangedEventArgs("UseSsl")); }
        }
    }
}
