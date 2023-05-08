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
        public int pulseLength = 50;
        public StimulationPulses(int stimulationLength)
        {
            if (stimulationLength > 500)
            {
                stimulationLength = 500;
            }
            pulseLength = stimulationLength;
        }
        public double[,] generatePulses(double factor, int selected_channel)
        {
            double[,] generatedPulses = new double[2, 500];
            Array.Clear(generatedPulses, 0, generatedPulses.Length);

            for (int i = 0; i < pulseLength; i++)
            {
                double val = (i % 5 == 0) ? 1.0 : 0.0;
                generatedPulses[selected_channel, i] = factor * val;
            }

            return generatedPulses;
        }
    }

    [Serializable]
    class StimulationControlFrame
    {
        public double normalised_current_level = -1;
        public int stimulation_length = -1;
        public int switch_channel = -1;
        public long ApplyControlData(AnalogMultiChannelWriter writer, Task analogOutTask, DigitalSingleChannelWriter digitalWriter, Int32 stimulationLength, int selected_channel = 0)
        {
            
            if (stimulation_length == -1) // use previous set stimulation length
            {
                stimulation_length = stimulationLength;
            }

            if (switch_channel != -1 && digitalWriter != null)
            {
                bool[] dataArray = new bool[8];
                for (int line = 0; line < 8; line++)
                {
                    dataArray[line] = true ? switch_channel == line : false;
                }
                digitalWriter.WriteSingleSampleMultiLine(true, dataArray);
            }

            if (normalised_current_level != -1)
            {
                StimulationPulses pulseSignalGenerator = new StimulationPulses(stimulation_length);

                analogOutTask.Stop();
                DateTimeOffset now = DateTimeOffset.UtcNow;
                writer.WriteMultiSample(false, pulseSignalGenerator.generatePulses(normalised_current_level, selected_channel));
                analogOutTask.Start();
                analogOutTask.WaitUntilDone();
                // Strange problem. Looks ok for now: https://forums.ni.com/t5/Multifunction-DAQ/WaitUntilDone-finishes-before-pulses-written-complete/td-p/4193057?profile.language=en
                Thread.Sleep(500);
                return now.ToUnixTimeMilliseconds();
            }

            return -1;
        }
    }

    [Serializable]
    class StimulationDataFrame
    {
        public double[] stimulation_current_loopback;
        public double[] stimulation_voltage;
        public long last_shock_on_device;

        private double[] GetChannelData(AnalogWaveform<double> waveform)
        {
            double[] samples = new double[waveform.Samples.Count];
            for (int idx = 0; idx < waveform.Samples.Count; idx++)
            {
                samples[idx] = waveform.Samples[idx].Value;
            }

            return samples;
        }

        public StimulationDataFrame(AnalogWaveform<double>[] buffer, long last_ts, int channel_offset = 0)
        {
            stimulation_current_loopback = GetChannelData(buffer[channel_offset]);
            stimulation_voltage = GetChannelData(buffer[1 + channel_offset]);
            last_shock_on_device = last_ts;
        }
    }

    [Serializable]
    class ChannelConfig
    {
        public string device_name;
        public string switch_channel_method;
    }

    class PainlabNIDS5Protocol : PainlabProtocol
    {
        static string descriptorPath = "Resources/device-descriptor.json";
        static string channelConfigPath = "Resources/channel-config.json";
        static double inSampleRate = 1000.0;
        static double outSampleRate = 1000.0;
        static Int32 NIBufferSize = 1000;
        static Int32 numSamplesPerFrame = 20;
        static double currentChannelMaxVolt = 10;
        static double outputChannelMaxVolt = 10;

        private ChannelConfig _channelConfig;
        private int _selected_channel;

        private Task _analogInTask;
        private Task _analogOutTask;
        private Task _digitalOutTask;
        private AnalogMultiChannelReader _analogInReader;
        private AsyncCallback _analogCallback;
        private AnalogWaveform<double>[] _buffer;
        private AnalogMultiChannelWriter _analogOutWriter;
        private DigitalSingleChannelWriter _digitalOutWriter;
        private bool _outputSuccessFlag = true;
        private Int32 _pulseLength = 50;

        private long _last_ts = -1;

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

            long applyResult = 0;
            if (_channelConfig.switch_channel_method == "dual")
            {
                applyResult = controlFrame.ApplyControlData(_analogOutWriter, _analogOutTask, null, _pulseLength, _selected_channel);
            }
            else
            {
                applyResult = controlFrame.ApplyControlData(_analogOutWriter, _analogOutTask, _digitalOutWriter, _pulseLength);
            }
            _last_ts = applyResult == -1 ? _last_ts : applyResult;
            _pulseLength = controlFrame.stimulation_length == -1 ? _pulseLength : controlFrame.stimulation_length;

            if (controlFrame.switch_channel != -1 && _channelConfig.switch_channel_method == "dual")
            {
                _selected_channel = controlFrame.switch_channel;
            }

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
            // Read Config
            _channelConfig = JsonConvert.DeserializeObject<ChannelConfig>
                (File.ReadAllText(channelConfigPath));

            /* Analog In Channel setup */
            _analogInTask = new Task();

            // Create a virtual channel
            _analogInTask.AIChannels.CreateVoltageChannel(
                _channelConfig.device_name + "/ai0",
                "StimulationCurrentLoopback",
                (AITerminalConfiguration)(-1)  /* -1 is default from NIDAQmx.h */,
                Convert.ToDouble(-currentChannelMaxVolt),
                Convert.ToDouble(currentChannelMaxVolt), 
                AIVoltageUnits.Volts);

            // Another channel for voltage
           _analogInTask.AIChannels.CreateVoltageChannel(
                "Dev1/ai1",
                "StimulationVoltage",
                (AITerminalConfiguration)(-1)  /* -1 is default from NIDAQmx.h */,
                Convert.ToDouble(-currentChannelMaxVolt),
                Convert.ToDouble(currentChannelMaxVolt), 
                AIVoltageUnits.Volts);

           if (_channelConfig.switch_channel_method == "dual")
           {
               // Second current channel
               _analogInTask.AIChannels.CreateVoltageChannel(
                   "Dev1/ai2",
                   "StimulationCurrentLoopback2",
                   (AITerminalConfiguration)(-1)  /* -1 is default from NIDAQmx.h */,
                   Convert.ToDouble(-currentChannelMaxVolt),
                   Convert.ToDouble(currentChannelMaxVolt),
                   AIVoltageUnits.Volts);

               // Second voltage channel
               _analogInTask.AIChannels.CreateVoltageChannel(
                    "Dev1/ai3",
                    "StimulationVoltage2",
                    (AITerminalConfiguration)(-1)  /* -1 is default from NIDAQmx.h */,
                    Convert.ToDouble(-currentChannelMaxVolt),
                    Convert.ToDouble(currentChannelMaxVolt),
                    AIVoltageUnits.Volts);
           }

            // Configure the timing parameters
            _analogInTask.Timing.ConfigureSampleClock("", inSampleRate,
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

            if (_channelConfig.switch_channel_method == "dual")
            {
                // Second voltage out channel
                _analogOutTask.AOChannels.CreateVoltageChannel(
                    "Dev1/ao1",
                    "StimulationOutput2",
                    Convert.ToDouble(-outputChannelMaxVolt),
                    Convert.ToDouble(outputChannelMaxVolt),
                    AOVoltageUnits.Volts);
            }

            // Configure the sample clock
            _analogOutTask.Timing.ConfigureSampleClock(
                String.Empty, /* means the internal clock */
                outSampleRate,
                SampleClockActiveEdge.Rising,
                SampleQuantityMode.FiniteSamples, 500);

            // Verify the task
            _analogOutTask.Control(TaskAction.Verify);

            // Write the data
            _analogOutWriter = new AnalogMultiChannelWriter(_analogOutTask.Stream);

            /* Digital write channel setup */
            _digitalOutTask = new Task();
            _digitalOutTask.DOChannels.CreateChannel("Dev1/Port0/line0:7", "",
                        ChannelLineGrouping.OneChannelForAllLines);
            _digitalOutWriter = new DigitalSingleChannelWriter(_digitalOutTask.Stream);
        }

        private byte[] PrepareDataFrameBytes()
        {
            int channelOffset = 0;
            if (_channelConfig.switch_channel_method == "dual")
            {
                channelOffset += _selected_channel * 2;
            }
            StimulationDataFrame dataFrame = new StimulationDataFrame(_buffer, _last_ts, channelOffset);
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

