using RepetierHostExtender.interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OctoConnect
{
    public partial class ConnectionPanel : UserControl
    {
        IHost host;
        Connector con;
        public ConnectionPanel(IHost _host)
        {
            host = _host;
            InitializeComponent();
        }

        public void Connect(Connector _con)
        {
            con = _con;
            bindingConnection.DataSource = con;
            bindingConnection_CurrentItemChanged(null, null);
        }

        private void textBoxApiKey_TextChanged(object sender, EventArgs e)
        {
            if (updating) return;
            con.Apikey = textBoxApiKey.Text;
        }

        private void textBoxHostname_TextChanged(object sender, EventArgs e)
        {
            if (updating) return;
            con.Hostname = textBoxHostname.Text;
        }

        private void numericUpDownPort_ValueChanged(object sender, EventArgs e)
        {
            if (updating) return;
            con.Port = (int)numericUpDownPort.Value;
        }

        private void checkBoxSsl_CheckedChanged(object sender, EventArgs e)
        {
            if (updating) return;
            con.UseSsl = checkBoxSsl.Checked;
        }

        bool updating;
        private void bindingConnection_CurrentItemChanged(object sender, EventArgs e)
        {
            updating = true;
            textBoxApiKey.Text = con.Apikey;
            textBoxHostname.Text = con.Hostname;
            numericUpDownPort.Value = con.Port;
            checkBoxSsl.Checked = con.UseSsl;
            updating = false;
        }
    }
}
