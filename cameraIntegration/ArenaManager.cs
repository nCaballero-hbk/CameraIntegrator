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

        private ConcurrentQueue<ArenaNET.IImage> imageQueue = new ConcurrentQueue<ArenaNET.IImage>(); 
        private ManualResetEventSlim queueHasImages = new ManualResetEventSlim(false);


        ArenaNET.IImage image = null;
        ArenaNET.IImage lastImage = null;
        SaveNET.VideoRecorder recorder = null;
        List<ArenaNET.IImage> images = new List<ArenaNET.IImage>();
        Thread streamThread_inner;

        private bool recording = false;
        private bool streaming = false;

        private const int WIDTH = 2448;
        private const int HEIGHT = 2048;
        private const double FPS = 18.0;
        private const String FILE_NAME = "C:/Users/NCABALLERO/OneDrive - HBK/Documents/VisualStudio_NET/test/Stream_Record/Stream_Record/Videos/video1.mp4";



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
        public System.Drawing.Bitmap GetBitMapImage(ArenaNET.IImage image)
        {
            if (m_converted != null)
            {
                ArenaNET.ImageFactory.Destroy(m_converted);
                m_converted = null;
            }

            m_converted = ArenaNET.ImageFactory.Convert(image, (ArenaNET.EPfncFormat)0x02200017);

            return m_converted.Bitmap;
        }


        private void PullImages()
        {
            ArenaNET.IImage pic = null;

            try
            {
                while (streaming == true)
                {
                    if (pic != null)
                    {
                        m_connectedDevice.RequeueBuffer(pic);
                    }

                    pic = m_connectedDevice.GetImage(2000);

                    // A copy of the Image needs to be done because we are trying to Dequeue in the other Thread
                    var picCopy = ArenaNET.ImageFactory.Copy(pic);

                    imageQueue.Enqueue(picCopy);
                    queueHasImages.Set();

                    if (recording == true)
                    {
                        images.Add(ArenaNET.ImageFactory.Convert(picCopy, ArenaNET.EPfncFormat.BGR8));

                    }
                }
            }
            catch(Exception e)
            {
                Console.WriteLine(e.ToString());
            }


        }

        public ArenaNET.IImage GetImageFromQueue()
        {

            queueHasImages.Wait();
            if (imageQueue.TryDequeue(out IImage result))
            {
                queueHasImages.Reset();
                return result;
            }
            else
            {
                return null;
            }
        }

        public void ClearQueue()
        {
            while (imageQueue.TryDequeue(out var _)){}
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
                    m_connectedDevice.StartStream();
                    streamThread_inner.Start();

                }
            }
        }

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

        public void StartRecording()
        {
            SaveNET.VideoParams parameters = new SaveNET.VideoParams(WIDTH, HEIGHT, FPS);
            recorder = new SaveNET.VideoRecorder(parameters, FILE_NAME);
            recorder.SetH264Mp4BGR8();
            recorder.Open();
            recording = true;
        }

        public void StopRecording()
        {
            recording = false;
            //recorder.Open();
            for (Int32 i = 0; i < images.Count; i++)
            {
                recorder.AppendImage(images[i].DataArray);
            }

            recorder.Close();
            for (Int32 i = 0; i < images.Count; i++)
                ArenaNET.ImageFactory.Destroy(images[i]);
        }

        // stops stream
        public void StopStream()
        {
            if (m_connectedDevice != null)
            {
                if ((m_connectedDevice.TLStreamNodeMap.GetNode("StreamIsGrabbing") as ArenaNET.IBoolean).Value == true)
                {
                    streaming = false;

                    m_connectedDevice.StopStream();

                    streamThread_inner.Abort();
                    streamThread_inner.Join();

                    lastImage = null;

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
