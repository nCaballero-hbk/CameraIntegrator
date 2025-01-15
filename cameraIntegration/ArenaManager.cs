using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using ArenaNET;
using System.Windows.Media.Media3D;
using System.Security.Cryptography;
using System.Threading;
using System.Linq;
using System.Drawing;
using System.Diagnostics;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Collections;
using System.Threading.Channels;
using System.Runtime.Remoting.Channels;


namespace Streaming
{
    // for each device in device list tree
    public class TreeItem
    {
        public string Title { get; set; } // string to display
        public string UId { get; set; } = ""; // serial number to identify device
    }

    

    public class ArenaManager
    {
        private ArenaNET.ISystem m_system = null;
        private List<ArenaNET.IDeviceInfo> m_deviceInfos;
        private ArenaNET.IDevice m_connectedDevice = null;
        private ArenaNET.IImage m_converted;


        // Try with Channels
        private Channel<ArenaNET.IImage> imageChannel = Channel.CreateBounded<ArenaNET.IImage>(new BoundedChannelOptions(8)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });


        SaveNET.VideoRecorder recorder = null;
        List<ArenaNET.IImage> images = new List<ArenaNET.IImage>();


        Thread streamThread_inner;


        private bool recording = false;
        private bool streaming = false;

        private const int WIDTH = 2448;
        private const int HEIGHT = 2048;
        private const double FPS = 18.0;
        private const String FILE_NAME = "C:/Users/NCABALLERO/OneDrive - HBK/Documents/VisualStudio_NET/test/Stream_Record/Stream_Record/Videos/video2.mp4";



        public ArenaManager()
        {
            m_system = ArenaNET.Arena.OpenSystem();
        }

        // connects to a device based on uid
        public void ConnectDevice(String UId)
        {
            if (m_connectedDevice == null)
            {
                UpdateDevices();

                for (int i = 0; i < m_deviceInfos.Count; i++)
                {
                    if (m_deviceInfos[i].SerialNumber == UId)
                    {
                        m_connectedDevice = m_system.CreateDevice(m_deviceInfos[i]);
                        return;
                    }
                }
            }

            throw new Exception();
        }

        // disconnects for connected device
        public void DisconnectDevice()
        {
            m_system.DestroyDevice(m_connectedDevice);
            m_connectedDevice = null;
        }

        // returns if connected to a device
        public bool DeviceConnected()
        {
            return (m_connectedDevice != null);
        }

        // gets uid of connected device
        public String ConnectedDeviceUId()
        {
            return ((ArenaNET.IString)m_connectedDevice.NodeMap.GetNode("DeviceSerialNumber")).Value;
        }

        // gets list of uids of available devices
        public List<String> GetDeviceUIds()
        {
            UpdateDevices();

            List<String> uids = new List<String>();

            for (int i = 0; i < m_deviceInfos.Count; i++)
            {
                uids.Add(m_deviceInfos[i].SerialNumber);
            }

            return uids;
        }

        // gets name of device
        public String GetDeviceName(String UId, String node)
        {
            for (int i = 0; i < m_deviceInfos.Count; i++)
            {
                if (m_deviceInfos[i].SerialNumber == UId)
                {
                    if (node == "DeviceUserID" || node == "UserDefinedName")
                        return m_deviceInfos[i].UserDefinedName;
                    else if (node == "DeviceModelName" || node == "ModelName")
                        return m_deviceInfos[i].ModelName;
                }
            }

            return "Invalid argument";
        }

        // check if IP addresses between device and interface match based on their subnets
        public bool IsNetworkValid(String UId)
        {
            UpdateDevices();

            for (int i = 0; i < m_deviceInfos.Count; i++)
            {
                if (m_deviceInfos[i].SerialNumber == UId)
                {
                    UInt32 ip = (UInt32)m_deviceInfos[i].IpAddress;
                    UInt32 subnet = (UInt32)m_deviceInfos[i].SubnetMask;

                    ArenaNET.IInteger ifipNode = (ArenaNET.IInteger)m_system
                            .GetTLInterfaceNodeMap(m_deviceInfos[i]).GetNode("GevInterfaceSubnetIPAddress");
                    ArenaNET.IInteger ifsubnetNode = (ArenaNET.IInteger)m_system
                            .GetTLInterfaceNodeMap(m_deviceInfos[i]).GetNode("GevInterfaceSubnetMask");
                    UInt32 ifip = (UInt32)ifipNode.Value;
                    UInt32 ifsubnet = (UInt32)ifsubnetNode.Value;

                    if (subnet != ifsubnet)
                        return false;

                    if ((ip & subnet) != (ifip & ifsubnet))
                        return false;

                    return true;
                }
            }

            throw new Exception();
        }


        // gets image as bitmap
        private System.Drawing.Bitmap GetBitMapImage(ArenaNET.IImage image)
        {
            if (m_converted != null)
            {
                // Every time ImageFactory is used we need to destroy that Image
                ArenaNET.ImageFactory.Destroy(m_converted);
                m_converted = null;
            }

            // Converts the image from the Channel in order to display it
            m_converted = ArenaNET.ImageFactory.Convert(image, (ArenaNET.EPfncFormat)0x02200017);

            return m_converted.Bitmap;
        }


        private void PullImages()
        {

            ArenaNET.IImage pic = null;

            var writer = imageChannel.Writer;

            try
            {
                while (streaming == true)
                {
                    // Get the image from the camera
                    pic = m_connectedDevice.GetImage(2000);

                    // A copy of the Image needs to be done because we are trying to grab the image from the channel.
                    // This copy will need to be destroyes because we used ImageFactory
                    var picCopy = ArenaNET.ImageFactory.Copy(pic);

                    // Write the copy in the channel and destroy the oldest in case is needed
                    while (!imageChannel.Writer.TryWrite(picCopy))
                    {
                        // Drop last item if it is full
                        imageChannel.Reader.TryRead(out var x);
                        Console.WriteLine($"Dropped last item {x}");
                        ImageFactory.Destroy(x);
                    }


                    if (recording == true)
                    {
                        // In case we are recording, we create another copy to save the image in the list of recorded images.
                        images.Add(ArenaNET.ImageFactory.Copy(pic));

                    }

                    // We requeue the initial image. Since is an image from the camera we don't need to destroy it but requeue it.
                    m_connectedDevice.RequeueBuffer(pic);
                }
            }
            catch(Exception e)
            {
                Console.WriteLine(e.ToString());
            }


        }

        public Bitmap GetImageFromChannel()
        {
            var reader = imageChannel.Reader;

            // We read the image from the Channel
            if (reader.TryRead(out var item))
            {
                var bitmap = GetBitMapImage(item);

                // We destroy that image because there is no longer needed and it comes from a copy
                ArenaNET.ImageFactory.Destroy(item);

                return bitmap;
            }
            else
            {
                return null;
            }

        }

        public void ClearChannel()
        {
            // Delete all the Images in the Channel
            while (imageChannel.Reader.TryRead(out var c))
            {
                ArenaNET.ImageFactory.Destroy(c);
            }
        }


        // starts stream
        public void StartStream()
        {
            if (m_connectedDevice != null)
            {
                if ((m_connectedDevice.TLStreamNodeMap.GetNode("StreamIsGrabbing") as ArenaNET.IBoolean).Value == false)
                {
                    (m_connectedDevice.TLStreamNodeMap.GetNode("StreamBufferHandlingMode") as ArenaNET.IEnumeration).Symbolic = "NewestOnly";
                    ConfigureSettings();

                    streaming = true;

                    streamThread_inner = new Thread(() => PullImages());

                    // Starts the stream in the device
                    m_connectedDevice.StartStream();

                    // Starts the Thread to make the PullImages work
                    streamThread_inner.Start();

                }
            }
        }

        // Function that configures the settings of the camera
        private void ConfigureSettings()
        {
            (m_connectedDevice.NodeMap.GetNode("AcquisitionMode") as ArenaNET.IEnumeration).FromString("Continuous");
            SetIntValue(m_connectedDevice.NodeMap, "Width", WIDTH);
            SetIntValue(m_connectedDevice.NodeMap, "Height", HEIGHT);

            // Set framerate
            (m_connectedDevice.NodeMap.GetNode("AcquisitionFrameRateEnable") as ArenaNET.IBoolean).Value = true;
            double fps = SetFloatValue(m_connectedDevice.NodeMap, "AcquisitionFrameRate", FPS);

            var streamAutoNegotiatePacketSizeNode = (ArenaNET.IBoolean)m_connectedDevice.TLStreamNodeMap.GetNode("StreamAutoNegotiatePacketSize");
            streamAutoNegotiatePacketSizeNode.Value = true;

            var streamPacketResendEnableNode = (ArenaNET.IBoolean)m_connectedDevice.TLStreamNodeMap.GetNode("StreamPacketResendEnable");
            streamPacketResendEnableNode.Value = true;
        }

        // Function to starts the recording. The recorder object needs to be created and Open in order to record the images.
        // Once the rceorder is open we can not change the settings or parameters of the recorder.
        public void StartRecording()
        {
            SaveNET.VideoParams parameters = new SaveNET.VideoParams(WIDTH, HEIGHT, FPS);
            recorder = new SaveNET.VideoRecorder(parameters, FILE_NAME);

            //Settings of the recorder
            recorder.SetH264Mp4BGR8();

            recorder.Open();
            recording = true;
        }


        // Function to stop the recording. It needs to be Async because if not it causes a delay in the streaming while saving all the images
        public async Task StopRecording()
        {
            recording = false;
            await Task.Run(() =>
            {
                for (Int32 i = 0; i < images.Count; i++)
                {
                    // Images need to be converted to match the recorder specifications. Since we are using Image factory we will need to destroy
                    var convertedItem = ArenaNET.ImageFactory.Convert(images[i], ArenaNET.EPfncFormat.BGR8);

                    // We append all the images on the recorder list into the recorder
                    recorder.AppendImage(convertedItem.DataArray);


                    ArenaNET.ImageFactory.Destroy(convertedItem);
                }

                // Once the recorder is closed it will save the vidoe in the directory specified and it will delete the recorder object
                recorder.Close();

                // Destry the images in the recorder list is needed
                for (Int32 i = 0; i < images.Count; i++)
                {
                    ArenaNET.ImageFactory.Destroy(images[i]);
                }

                // Clear the list
                images.Clear();
            });

        }


        // stops stream
        public void StopStream()
        {
            if (m_connectedDevice != null)
            {
                if ((m_connectedDevice.TLStreamNodeMap.GetNode("StreamIsGrabbing") as ArenaNET.IBoolean).Value == true)
                {
                    streaming = false;

                    // Strop Stream in the device
                    m_connectedDevice.StopStream();


                    // Stop the thread with the PullImages function
                    streamThread_inner.Abort();
                    streamThread_inner.Join();


                }
            }
        }

        // updates list of available devices
        private void UpdateDevices()
        {
            m_system.UpdateDevices(100);
            m_deviceInfos = m_system.Devices;
        }

        private static Int64 SetIntValue(ArenaNET.INodeMap nodeMap, String nodeName, Int64 value)
        {
            // get node
            ArenaNET.IInteger integer = (ArenaNET.IInteger)nodeMap.GetNode(nodeName);

            // Ensure increment
            //    If a node has an increment (all integer nodes & some float
            //    nodes), only multiples of the increment can be set.
            value = (((value - integer.Min) / integer.Inc) * integer.Inc) + integer.Min;

            // Check min/max values
            //    Values must not be less than the minimum or exceed the maximum value of
            //    a node. If a value does so, push it within range.
            if (value < integer.Min)
                value = integer.Min;

            if (value > integer.Max)
                value = integer.Max;

            // set value
            integer.Value = value;

            // return value for output
            return value;
        }

        private static Double SetFloatValue(ArenaNET.INodeMap nodeMap, String nodeName, Double value)
        {
            // get node
            ArenaNET.IFloat floatNode = (ArenaNET.IFloat)nodeMap.GetNode(nodeName);

            // Check min/max values
            //    Values must not be less than the minimum or exceed the maximum
            //    value of a node. If a value does so, push it within
            //    range.
            if (value < floatNode.Min)
                value = floatNode.Min;

            if (value > floatNode.Max)
                value = floatNode.Max;

            // set value
            floatNode.Value = value;

            // return value for output
            return value;
        }
    }
}
