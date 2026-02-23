using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DataRippleAIDesktop.Utilities
{
    public static class RecorderUtility
    {
        // Helper method to get the device index by name
        public static int GetInputDeviceIndexByName(string inpDeviceName)
        {
            int deviceCount = 0;
            
                deviceCount = WaveInEvent.DeviceCount;
                for (int i = 0; i < deviceCount; i++)
                {
                    var deviceCapabilities = WaveInEvent.GetCapabilities(i);
                    if (deviceCapabilities.ProductName.Equals(inpDeviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
          
            // Return -1 if the device is not found
            return -1;
        }

        public static string GetInputDeviceNameByIndex(int inpDeviceIndex)
        {
            int deviceCount = 0;

            deviceCount = WaveInEvent.DeviceCount;
            var deviceCapabilities = WaveInEvent.GetCapabilities(inpDeviceIndex);

            return deviceCapabilities.ProductName;
        }

        public static MMDevice GetOutPutDeviceByDeviceName(string outDeviceName)
        {
            MMDevice device = null;
            // Create an instance of MMDeviceEnumerator to enumerate devices
            MMDeviceEnumerator deviceEnumerator = new MMDeviceEnumerator();
            MMDeviceCollection mMDevices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            int deviceCount = mMDevices.Count;
            foreach (MMDevice mMDevice in mMDevices)
            {
              
                if (mMDevice.FriendlyName.Equals(outDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return mMDevice;
                }
            }

            //Return null if the device is not found
            return null;
        }
    }
}
