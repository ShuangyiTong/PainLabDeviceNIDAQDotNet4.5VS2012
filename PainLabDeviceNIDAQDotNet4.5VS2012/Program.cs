using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

using NationalInstruments.DAQmx;
using NationalInstruments;

namespace PainLabDeviceNIDAQDotNet4._5VS2012
{
    class StimulationPulses
    {
        public static double[] pulses = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0 };

        public double[] generatePulses(double factor)
        {
            double[] generatedPulses = new double[pulses.Length];

            for (int i = 0; i < pulses.Length; i++)
            {
                generatedPulses[i] = factor * pulses[i];
            }

            return generatedPulses;
        }
    }

    [Serializable]
    class StimulationControlFrame
    {
        public double normalised_current_level;
        public void ApplyControlData(AnalogSingleChannelWriter writer, Task outTask)
        {
            StimulationPulses pulseSignalGenerator = new StimulationPulses();

            outTask.Stop();
            writer.WriteMultiSample(false, pulseSignalGenerator.generatePulses(normalised_current_level));
            outTask.Start();
            outTask.WaitUntilDone();
            // Strange problem. Looks ok for now: https://forums.ni.com/t5/Multifunction-DAQ/WaitUntilDone-finishes-before-pulses-written-complete/td-p/4193057?profile.language=en
            Thread.Sleep(StimulationPulses.pulses.Length);
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
            stimulation_current_loopback = GetChannelData(buffer[0]);
        }
    }

    class PainlabNIDS5Protocol : PainlabProtocol
    {
        static string descriptorPath = "Resources/device-descriptor.json";
        static double sampleRate = 1000.0;
        static Int32 NIBufferSize = 1000;
        static Int32 numSamplesPerFrame = 20;
        static double currentChannelMaxVolt = 10;
        static double outputChannelMaxVolt = 10;

        private Task _analogInTask;
        private Task _analogOutTask;
        private AnalogMultiChannelReader _analogInReader;
        private AsyncCallback _analogCallback;
        private AnalogWaveform<double>[] _buffer;
        private AnalogSingleChannelWriter _analogOutWriter;
        private bool _outputSuccessFlag = true;

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

            controlFrame.ApplyControlData(_analogOutWriter, _analogOutTask);
            if (_outputSuccessFlag != true)
            {
                _outputSuccessFlag = true; // set back to true
                throw new PainlabProtocolException("Failed to apply control");
            }
        }

        public void ControlApplicationThread()
        {
            while (true)
            {
                _waitOnControlSem.WaitOne();
                HandlingControlData();
            }
        }

        public void setupNIChannel()
        {
            /* Analog In Channel setup */
            _analogInTask = new Task();

            // Create a virtual channel
            _analogInTask.AIChannels.CreateVoltageChannel(
                "Dev1/ai0",
                "StimulationCurrentLoopback",
                (AITerminalConfiguration)(-1)  /* -1 is default from NIDAQmx.h */,
                Convert.ToDouble(-currentChannelMaxVolt),
                Convert.ToDouble(currentChannelMaxVolt), 
                AIVoltageUnits.Volts);

            // Configure the timing parameters
            _analogInTask.Timing.ConfigureSampleClock("", sampleRate,
                SampleClockActiveEdge.Rising, SampleQuantityMode.ContinuousSamples, NIBufferSize);

            // Verify the Task
            _analogInTask.Control(TaskAction.Verify);

            _analogInReader = new AnalogMultiChannelReader(_analogInTask.Stream);
            _analogCallback = new AsyncCallback(AnalogInCallback);

            // Use SynchronizeCallbacks to specify that the object 
            // marshals callbacks across threads appropriately.
            _analogInReader.SynchronizeCallbacks = true;
            _analogInReader.BeginReadWaveform(numSamplesPerFrame,
                _analogCallback, _analogInTask);

            /* Analog Out Channel setup */
            _analogOutTask = new Task();

            // Create a virtual channel
            _analogOutTask.AOChannels.CreateVoltageChannel(
                "Dev1/ao0",
                "StimulationOutput",
                Convert.ToDouble(-outputChannelMaxVolt),
                Convert.ToDouble(outputChannelMaxVolt),
                AOVoltageUnits.Volts);

            // Configure the sample clock
            _analogOutTask.Timing.ConfigureSampleClock(
                String.Empty, /* means the internal clock */
                sampleRate,
                SampleClockActiveEdge.Rising,
                SampleQuantityMode.FiniteSamples, StimulationPulses.pulses.Length);

            // Verify the task
            _analogOutTask.Control(TaskAction.Verify);

            // Write the data
            _analogOutWriter = new AnalogSingleChannelWriter(_analogOutTask.Stream);
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
                    _analogCallback, _analogInTask, _buffer);
            }
            catch (DaqException exception)
            {
                // Display Errors
                DebugOutput("DAQ exception" + exception.Message);
                _analogInTask.Dispose();
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

            protocol.Init(netConf, waitOnControl: true);
            protocol.setupNIChannel();
            Thread controlThread = new Thread(new ThreadStart(protocol.ControlApplicationThread));
            controlThread.Start();
            Console.WriteLine("setup complete");

            Console.WriteLine("Collecting Data...Press Enter to Exit");
            string waitInput = Console.ReadLine();
        }
    }
}

