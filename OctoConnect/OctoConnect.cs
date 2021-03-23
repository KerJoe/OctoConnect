using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using RepetierHostExtender.basic;
using RepetierHostExtender.interfaces;
using RepetierHostExtender.utils;

namespace OctoConnect
{
    public class OctoConnect : IHostPlugin
    {
        IHost host;
        PrinterConnectorBase connector;
        public OctoConnect()
        {

        }

        public void PreInitalize(IHost _host)
        {
            host = _host;
            connector = new Connector(host);
            host.ActivePrinter.AddConnector(connector);
        }

        public void PostInitialize()
        {
            //throw new NotImplementedException();
        }

        public void FinializeInitialize()
        {
            //throw new NotImplementedException();
        }
    }
}
