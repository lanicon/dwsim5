﻿using System;
using System.Collections.Generic;
using Eto.Forms;
using Eto.Drawing;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using DWSIM.Thermodynamics.BaseClasses;
using System.Diagnostics;

namespace DWSIM.UI.Forms
{
    partial class SplashScreen : Form
    {

        private double sf = GlobalSettings.Settings.UIScalingFactor;

        public MainForm MainFrm;

        private Label lblMessage;

        void InitializeComponent()
        {

            WindowStyle = Eto.Forms.WindowStyle.None;

            Style = "transparent-form";

            Title = "DWSIM";

            string imgprefix = "DWSIM.UI.Forms.Resources.Icons.";

            int dx, dy, w, h;

            if (Application.Instance.Platform.IsGtk)
            {
                w = (int)(sf * 916);
                h = (int)(sf * 426);
                dx = 83;
                dy = 32;
            }
            else
            {
                w = 1088;
                h = 509;
                dx = 0;
                dy = 0;
            }

            ClientSize = new Size(w, h);

            var lbl1 = new Label { Style = "splashlabels2", Text = "ApplicationVersion".Localize() + Assembly.GetExecutingAssembly().GetName().Version.ToString() };

            var lbl1a = new Label { Style = "splashlabels1", Text = "Version".Localize() + " " + Assembly.GetExecutingAssembly().GetName().Version.Major.ToString() + "." + Assembly.GetExecutingAssembly().GetName().Version.Minor.ToString() };

            var updfile = AppDomain.CurrentDomain.BaseDirectory + Path.DirectorySeparatorChar + "version.info";

            if (File.Exists(updfile))
            {
                int vinfo = 0;
                int.TryParse(File.ReadAllText(updfile), out vinfo);
                if (vinfo > 0) lbl1a.Text += " Update " + vinfo;
            }

            var lbl2 = new Label { Style = "splashlabels2", Text = SharedClasses.Utility.GetRuntimeVersion() };

            var lbl3 = new Label { Style = "fixedwidth", Text = "GPLNotice".Localize() };

            lbl1.TextColor = Colors.White;
            lbl2.TextColor = Colors.White;
            lbl1a.TextColor = new Color(0.051f, 0.447f, 0.651f);
            lbl3.TextColor = new Color(0.051f, 0.447f, 0.651f);

            lbl3.Width = (int)(sf * 576);
            lbl3.Height = (int)(sf * 40);

            lblMessage = new Label { Style = "splashlabels2", Text = "LoadingComponents".Localize() };

            lblMessage.TextColor = Colors.White;

            var lbl5 = new Label { Style = "splashlabels1", Text = Shared.AssemblyCopyright };

            lbl5.TextColor = new Color(0.051f, 0.447f, 0.651f);

            var layout = new PixelLayout();

            var img = new ImageView { Image = Bitmap.FromResource(imgprefix + "DWSIM_splash.png") };

            img.Width = Width;
            img.Height = Height;

            layout.Add(img, (int)(sf *( 0 - dx)), (int)(sf * (0 - dy)));
            layout.Add(lblMessage, (int)(sf * (101 - dx)), (int)(sf * (185 - dy)));
            layout.Add(lbl1, (int)(sf * (101 - dx)), (int)(sf * (381 - dy)));
            layout.Add(lbl2, (int)(sf * (101 - dx)), (int)(sf * (403 - dy)));
            layout.Add(lbl1a, (int)(sf * (419 - dx)), (int)(sf * (185 - dy)));
            layout.Add(lbl5, (int)(sf * (419 - dx)), (int)(sf * (213 - dy)));
            layout.Add(lbl3, (int)(sf * (419 - dx)), (int)(sf * (381 - dy)));

            Content = layout;

            var center = Screen.PrimaryScreen.WorkingArea.Center;
            center.X -= w / 2;
            center.Y -= h / 2;

            Location = new Point(center);

            Topmost = true;

            img.BackgroundColor = Colors.Transparent;
            layout.BackgroundColor = Colors.Transparent;
            if (Application.Instance.Platform.IsWinForms)
            {
                BackgroundColor = Colors.White;
            }
            else
            {
                BackgroundColor = Colors.Transparent;
            }

            Shown += SplashScreen_Shown;

            ShowInTaskbar = false;

        }

        void SplashScreen_Shown(object sender, EventArgs e)
        {
            Task.Factory.StartNew(() =>
            {
                Thread.Sleep(2000);
                PerformExtraTasks();
            }).ContinueWith((t) => Application.Instance.Invoke(() =>
            {
                this.Close();
                var currver = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                if (GlobalSettings.Settings.CurrentVersion != currver)
                {
                    GlobalSettings.Settings.CurrentVersion = currver;
                    var wntext = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "whatsnew.txt"));
                    MessageBox.Show(wntext, "What's New", MessageBoxButtons.OK, MessageBoxType.Information, MessageBoxDefaultButton.OK);
                }
                CheckForUpdates();
            }));
        }

        void PerformExtraTasks()
        {


            //Read user compounds

            Application.Instance.AsyncInvoke(() => lblMessage.Text = "Reading User Compounds...");

            foreach (var path in GlobalSettings.Settings.UserDatabases)
            {
                try
                {
                    if (Path.GetExtension(path).ToLower() == ".xml")
                    {
                        var comps = DWSIM.Thermodynamics.Databases.UserDB.ReadComps(path);
                        MainFrm.UserCompounds.AddRange(comps);
                    }
                    else if (Path.GetExtension(path).ToLower() == ".json")
                    {
                        var comp = Newtonsoft.Json.JsonConvert.DeserializeObject<ConstantProperties>(File.ReadAllText(path));
                        MainFrm.UserCompounds.Add(comp);
                    }
                }
                catch { }
            }
            
        }

        void CheckForUpdates()
        {

            //check for updates

            Task.Factory.StartNew(() =>
            {
                var updfile = AppDomain.CurrentDomain.BaseDirectory + Path.DirectorySeparatorChar + "version.info";
                string uinfo = "0";
                if (File.Exists(updfile)) uinfo = File.ReadAllText(updfile);
                GlobalSettings.Settings.CurrentRunningVersion = Assembly.GetExecutingAssembly().GetName().Version.Major.ToString() + "." + Assembly.GetExecutingAssembly().GetName().Version.Minor.ToString() + "." + uinfo;
                Console.WriteLine("Current Version: " + GlobalSettings.Settings.CurrentRunningVersion);
                return SharedClasses.UpdateCheck.CheckForUpdates();
            }).ContinueWith((t) =>
            {
                if (t.Result)
                {
                    var whatsnew = SharedClasses.UpdateCheck.GetWhatsNew();
                    Application.Instance.Invoke(() =>
                    {
                        if (MessageBox.Show("An updated version is available to download from the official website. Update DWSIM to fix bugs, crashes and take advantage of new features.\n\n" + whatsnew, "Update Available", MessageBoxButtons.OKCancel, MessageBoxType.Information, MessageBoxDefaultButton.OK) == DialogResult.Ok)
                        {
                            Process.Start("http://dwsim.inforside.com.br/wiki/index.php?title=Downloads#DWSIM_for_Desktop_Systems");
                        }
                    });
                }
            });

        }

    }
}
