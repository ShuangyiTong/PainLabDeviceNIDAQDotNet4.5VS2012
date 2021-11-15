using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

using NationalInstruments.DAQmx;
using NationalInstruments;

namespace PainLabDeviceNIDAQDotNet4._5VS2012
{
    [Serializable]
    class StimulationControlFrame
    {
        public double normalised_current_level;
        public void ApplyControlData()
        {
        }
    }

    [Serializable]
    class StimulationDataFrame
    {
        public double[] stimulation_current_loopback;

        private double[] GetChannelData(AnalogWaveform<double> waveform)
        {
            double[] samples = new double[waveform.Samples.Count];
            for (int idx = 0; idx < waveform.Samples.Count; idx++)
            {
                samples[idx] = waveform.Samples[idx].Value;
            }

            return samples;
        }

        public StimulationDataFrame(AnalogWaveform<double>[] buffer)
        {
            double[] stimulation_current_loopback = GetChannelData(buffer[0]);
        }
    }

    class PainlabNIDS5Protocol : PainlabProtocol
    {
        static string descriptorPath = "Resources/device-descriptor.json";
        static double sampleRate = 1000.0;
        static Int32 NIBufferSize = 1000;
        static Int32 numSamplesPerFrame = 10;
        static double maxVolt = 10;

        private Task _NITask;
        private AnalogMultiChannelReader _analogInReader;
        private AsyncCallback _analogCallback;
        private AnalogWaveform<double>[] _buffer;

        protected override void RegisterWithDescriptor()
        {
            string descriptorString = File.ReadAllText(descriptorPath);
            SendString(descriptorString);

            return;
        }
        protected override void ApplyControlData()
        {
            StimulationControlFrame controlFrame 
                    = JsonConvert.DeserializeObject<StimulationControlFrame>
                      (Encoding.UTF8.GetString(_controlBuffer, 0, (int)_numControlBytes));

            controlFrame.ApplyControlData();
        }

        public void setupNIChannel()
        {
            _NITask = new Task();

            // Create a virtual channel
            _NITask.AIChannels.CreateVoltageChannel("Dev1/ai0", "StimulationCurrentLoopback",
                (AITerminalConfiguration)(-1)  /* -1 is default from NIDAQmx.h */, Convert.ToDouble(-maxVolt),
                maxVolt, AIVoltageUnits.Volts);

            // Configure the timing parameters
            _NITask.Timing.ConfigureSampleClock("", sampleRate,
                SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, NIBufferSize);

            // Verify the Task
            _NITask.Control(TaskAction.Verify);

            _analogInReader = new AnalogMultiChannelReader(_NITask.Stream);
            _analogCallback = new AsyncCallback(AnalogInCallback);

            // Use SynchronizeCallbacks to specify that the object 
            // marshals callbacks across threads appropriately.
            _analogInReader.SynchronizeCallbacks = true;
            _analogInReader.BeginReadWaveform(numSamplesPerFrame,
                _analogCallback, _NITask);
        }

        private byte[] PrepareDataFrameBytes()
        {
            StimulationDataFrame dataFrame = new StimulationDataFrame(_buffer);
            byte[] byteData = StringToBytes(JsonConvert.SerializeObject(dataFrame, Formatting.None));

            return byteData;
        }

        private void AnalogInCallback(IAsyncResult ar)
        {
            try
            {
                // Read the available data from the channels
                _buffer = _analogInReader.EndReadWaveform(ar);

                // TODO: process data and call updateFramedata
                byte[] byteData = PrepareDataFrameBytes();
                UpdateFrameData(byteData);
                _analogInReader.BeginMemoryOptimizedReadWaveform(numSamplesPerFrame,
                    _analogCallback, _NITask, _buffer);
            }
            catch (DaqException exception)
            {
                // Display Errors
                DebugOutput("DAQ exception" + exception.Message);
                _NITask.Dispose();
            }
        }
    }

    class Program
    {
        static string networkConfigPath = "Resources/network-config.json";
        static void Main(string[] args)
        {
            PainlabNIDS5Protocol protocol = new PainlabNIDS5Protocol();

            string networkJsonString = File.ReadAllText(networkConfigPath);
            NetworkConfig netConf = JsonConvert.DeserializeObject<NetworkConfig>(networkJsonString);

            protocol.Init(netConf);
            Console.WriteLine("setup complete");
            protocol.setupNIChannel();

            Console.WriteLine("Collecting Data...Press Enter to Exit");
            string waitInput = Console.ReadLine();
        }
    }
}

