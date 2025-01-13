﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.IO;
using System.Threading;
using System.Drawing;
using System.Windows.Media;
using System.Threading.Channels;


namespace Streaming
{
    /// <summary>
    /// Interaction logic for StreamingWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        ArenaManager Arena;
        Thread streamThread_outer;
        Boolean stream = false;
        Boolean record = false;
        BitmapImage bitmapimage;

        // on example start up
        public MainWindow()
        {
            Arena = new ArenaManager();

            InitializeComponent();
            UpdateDeviceList();

            stopS_btn.IsEnabled = false;
            startS_btn.IsEnabled = true;
            startR_btn.IsEnabled = false;
            stopR_btn.IsEnabled = false;
        }

        // refresh button handler
        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            UpdateDeviceList();
        }

        // thread for grabbing images
        private void StreamThread()
        {
            while (stream)
            {

                // grab image
                GetImage();

            }
        }

        // grab image from stream to display to window
        private void GetImage()
        {
            this.Dispatcher.Invoke(() =>
            {
                // if connected to selected tree view device
                if (Arena.DeviceConnected())
                {
                    try
                    {
                        //ArenaNET.IImage image = Arena.GetImageFromQueue();

                        //ArenaNET.IImage converted = ArenaNET.ImageFactory.Convert(image, (ArenaNET.EPfncFormat)0x02200017);
                        
                        ArenaNET.IImage image = Arena.GetImageFromQueue();

                        System.Drawing.Bitmap bitmap = Arena.GetBitMapImage(image);

                        using (MemoryStream memory = new MemoryStream())
                        {
                            // save bitmap to memory stream
                            bitmap.Save(memory, System.Drawing.Imaging.ImageFormat.Bmp);
                            memory.Position = 0;
                            // create bitmap image
                            bitmapimage = new BitmapImage();
                            bitmapimage.BeginInit();
                            bitmapimage.StreamSource = memory;
                            bitmapimage.CacheOption = BitmapCacheOption.OnLoad;
                            bitmapimage.EndInit();

                            // set Window image source to be 
                            Image.Source = bitmapimage;
                            bitmap.Dispose();
                        }
                    }
                    catch
                    {
                        CleanUpStream();
                    }
                }
            });
        }

        // start stream button handler
        private void StartStream_Click(object sender, RoutedEventArgs e)
        {
            if (stream == false)
            {
                stopS_btn.IsEnabled = true;
                startS_btn.IsEnabled = false;
                startR_btn.IsEnabled = true;
                stopR_btn.IsEnabled = false;

                Arena.StartStream();
                stream = true;

                // start streaming thread for acquiring images
                streamThread_outer = new Thread(() => StreamThread());
                streamThread_outer.Start();

            }
        }

        // stop stream button handler
        private void StopStream_Click(object sender, RoutedEventArgs e)
        {
            if (stream == true)
            {
                stopS_btn.IsEnabled = false;
                startS_btn.IsEnabled = true;
                startR_btn.IsEnabled = false;
                stopR_btn.IsEnabled = false;

                Arena.StopStream();
                
                stream = false;

                //Clear the Queue
                Arena.ClearQueue();

                // abort thread and wait for it to stop
                // Wait for the thread to finish
                streamThread_outer.Abort();
                streamThread_outer.Join();

                // stop stream and set window image source to empty
                //stream = false;
                bitmapimage = null;
                Image.Source = null;
            }
        }

        // Start recording button handler
        private void StartRecording_Click(object sender, RoutedEventArgs e)
        {
            stopS_btn.IsEnabled = false;
            startS_btn.IsEnabled = false;
            startR_btn.IsEnabled = false;
            stopR_btn.IsEnabled = true;

            if (stream == true && record == false)
            {
                record = true;
                Arena.StartRecording();
            }
        }

        private void StopRecording_Click(Object sender, RoutedEventArgs e)
        {
            stopS_btn.IsEnabled = true;
            startS_btn.IsEnabled = false;
            startR_btn.IsEnabled = true;
            stopR_btn.IsEnabled = false;

            if (stream == true && record == true)
            {
                record = false;
                Arena.StopRecording();
            }
        }

        // tree view item selected handler
        private void DeviceTreeView_OnItemSelected(object sender, RoutedEventArgs e)
        {
            try
            {
                if (Arena.DeviceConnected())
                    Arena.DisconnectDevice();

                TreeViewItem treeViewItem = e.OriginalSource as TreeViewItem;

                if (treeViewItem != null)
                {
                    TreeItem treeItem = treeViewItem.DataContext as TreeItem;
                    Arena.ConnectDevice(treeItem.UId);
                }
            }
            catch
            {
                // do nothing
            }
        }

        // update currently displayed devices in the treeView
        private void UpdateDeviceList()
        {
            List<String> UIds = Arena.GetDeviceUIds();

            for (int i = 0; i < UIds.Count; i++)
            {
                // if device found in updated devices is not in tree
                if (!DeviceTreeView.Items.Cast<TreeItem>().Any(item => item.UId == UIds[i]))
                {
                    if (Arena.IsNetworkValid(UIds[i]))
                    {
                        try
                        {
                            Arena.ConnectDevice(UIds[i]);
                            DeviceTreeView.Items.Add(new TreeItem()
                            {
                                Title = (Arena.GetDeviceName(UIds[i], "DeviceUserID") != "" ?
                                        Arena.GetDeviceName(UIds[i], "DeviceUserID") :
                                        Arena.GetDeviceName(UIds[i], "DeviceModelName")),
                                UId = UIds[i]
                            });
                        }
                        catch
                        {
                            DeviceTreeView.Items.Add(new TreeItem()
                            {
                                Title = (Arena.GetDeviceName(UIds[i], "DeviceUserID") != "" ?
                                        Arena.GetDeviceName(UIds[i], "DeviceUserID") :
                                        Arena.GetDeviceName(UIds[i], "DeviceModelName")) + " (unable to connect)",
                                UId = UIds[i]
                            });
                        }
                    }
                    else
                    {
                        DeviceTreeView.Items.Add(new TreeItem()
                        {
                            Title = (Arena.GetDeviceName(UIds[i], "DeviceUserID") != "" ?
                                    Arena.GetDeviceName(UIds[i], "DeviceUserID") :
                                    Arena.GetDeviceName(UIds[i], "DeviceModelName")) + " (invalid network settings)",
                            UId = UIds[i]
                        });
                    }
                }
            }

            for (int i = 0; i < DeviceTreeView.Items.Count; i++)
            {
                TreeItem item = DeviceTreeView.Items[i] as TreeItem;

                // if device in tree no longer in updated devices, remove it from tree
                if (!UIds.Any(UId => UId == item.UId))
                {
                    if (Arena.ConnectedDeviceUId() == item.UId)
                        Arena.DisconnectDevice();

                    // remove device information from device tree view
                    DeviceTreeView.Items.Remove(item);
                }
            }
        }

        // clean up stream and disconnect from device
        private void CleanUpStream()
        {
            this.Dispatcher.Invoke(() =>
            {
                if (Arena.DeviceConnected())
                {
                    // abort thread and wait for it to stop
                    streamThread_outer.Abort();
                    streamThread_outer.Join();
                    stream = false;

                    // stop stream
                    Arena.StopStream();

                    // set window image source to empty
                    bitmapimage = null;
                    Image.Source = null;
                }

                UpdateDeviceList();
            });
        }

        // window close button handler
        private void WindowClosing_Event(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // abort thread and wait for it to stop
            try
            {
                streamThread_outer.Abort();
                streamThread_outer.Join();
            }
            catch
            {
                // do nothing
            }
        }
    }
}