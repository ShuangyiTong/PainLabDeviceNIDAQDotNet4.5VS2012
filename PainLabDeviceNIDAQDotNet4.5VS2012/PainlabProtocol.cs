using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System.Text;

public class PainlabProtocolException : Exception
{
    public PainlabProtocolException()
    {
    }

    public PainlabProtocolException(string message)
        : base(message)
    {
    }

    public PainlabProtocolException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

[Serializable]
public class NetworkConfig
{
    public string host;
    public int port;
    public UInt32 maxFrameBuffer;
}

public class PainlabProtocol
{
    private static Semaphore _sendSync = null;
    private TcpClient _connection = null;
    private Thread _listenerThread = null;
    private Thread _senderThread = null;

    protected UInt32 _bufferSize = 0;
    protected Mutex _queueBufferLock = null;
    protected Semaphore _queueBufferSem = null;
    protected UInt32 _queueNumBytes = 0;
    protected byte[] _queueBuffer = null;
    private UInt32 _sendNumBytes = 0;
    private byte[] _sendBuffer = null;
    private Mutex _sendLock = null;
    protected byte[] _listenBuffer = null;
    protected UInt32 _numControlBytes = 0;
    protected byte[] _controlBuffer = null;

    protected virtual void DebugOutput(string msg)
    {
        Console.WriteLine(msg);
    }

    public void Init(NetworkConfig netConf)
    {
        _bufferSize = netConf.maxFrameBuffer;
        _queueBuffer = new byte[_bufferSize];
        _sendBuffer = new byte[_bufferSize];
        _listenBuffer = new byte[_bufferSize];
        _controlBuffer = new byte[_bufferSize];

        _sendSync = new Semaphore(1, 1);
        _queueBufferLock = new Mutex();
        _queueBufferSem = new Semaphore(0, 1);
        _sendLock = new Mutex();

        try
        {
            _connection = new TcpClient(((NetworkConfig)netConf).host,
                                       ((NetworkConfig)netConf).port);
        }
        catch (SocketException socketException)
        {
            DebugOutput("Socket exception: " + socketException.Message);
            return;
        }

        _listenerThread = new Thread(new ThreadStart(ListenThread));
        _listenerThread.Start();

        _senderThread = new Thread(new ThreadStart(SenderThread));
        _senderThread.Start();
    }

    void ListenThread()
    {
        try
        {
            byte[] listenBuffer = new byte[_bufferSize];
            try
            {
                NetworkStream stream = _connection.GetStream();
                while (true)
                {
                    UInt32 totalBytes = 0;
                    int length = 0;
                    int offset = 0;
                    while ((length = stream.Read(_listenBuffer, offset, _listenBuffer.Length - offset)) != 0 || length > 0)
                    {
                        offset += length;

                        if (totalBytes == 0)
                        {
                            // new transmission
                            if (offset < 4)
                            {
                                length = 0;
                                continue;
                            }

                            Array.Reverse(_listenBuffer, 0, 4);
                            totalBytes = BitConverter.ToUInt32(_listenBuffer, 0);
                        }

                        if (offset < (int)totalBytes + 4)
                        {
                            length = 0;
                            continue;
                        }
                        else
                        {
                            int extraBytes = offset - (int)totalBytes - 4;
                            bool handled = false;

                            if (totalBytes < 5)
                            {
                                // check if it is a reply
                                string reply = Encoding.UTF8.GetString(_listenBuffer, 4, (int)totalBytes);
                                if (reply == "OK")
                                {
                                    // signal sender thread to continue
                                    _sendSync.Release();
                                    handled = true;
                                }
                                else if (reply == "FAIL")
                                {
                                    DebugOutput("Server side failure");
                                    handled = true;
                                }
                            }

                            if (!handled)
                            {
                                // going into this section must be control data
                                Array.Copy(_listenBuffer, 4, _controlBuffer, 0, (int)totalBytes);
                                _numControlBytes = (UInt32)totalBytes;
                            }

                            // handle sticky packets
                            if (extraBytes > 0)
                            {
                                // Copy to the beginning of listen buffer
                                Array.Copy(_listenBuffer, (int)totalBytes + 4, _listenBuffer, 0, extraBytes);
                                offset = extraBytes;
                                // Reset totalBytes so that it knows to decode length first
                                totalBytes = 0;
                                // Set length so the next loop can proceed processing the remaining part if not getting new data
                                length = extraBytes;
                            }
                            else
                            {
                                length = 0;
                            }

                            break;
                        }
                    }
                }
            }
            catch (SocketException socketException)
            {
                DebugOutput("Socket exception: " + socketException);
            }
        }
        catch (PainlabProtocolException e)
        {
            DebugOutput("Painlab exception: " + e.Message);
            return;
        }
    }

    protected void SendData(byte[] data, UInt32 l=0)
    {
        if (_connection == null)
        {
            return;
        }

        try
        {
            NetworkStream stream = _connection.GetStream();
            if (stream.CanWrite)
            {
                UInt32 dataLength = (UInt32)data.Length;
                if (Convert.ToBoolean(l))
                {
                    dataLength = l;
                }
                byte[] dataLengthBE = BitConverter.GetBytes(dataLength);
                Array.Reverse(dataLengthBE);

                // Use lock because the main thread will invoke send reply data
                _sendLock.WaitOne();
                // TODO: combine these two calls into one? Are they directly invoke syscall? Can I remove lock if combined in one call?
                // It will certainly valid to remove this lock if we stop replying control data command in main thread's Update()
                // But we can't do that in the fly because the queuebuffer allows one piece of data each time (the server would have no problem with that)
                stream.Write(dataLengthBE, 0, 4);
                stream.Write(data, 0, (int)dataLength);
                _sendLock.ReleaseMutex();
            }
        }
        catch (SocketException socketException)
        {
            DebugOutput("Socket exception: " + socketException.Message);
            return;
        }
    }

    protected void SendString(string stringData)
    {
        byte[] stringBytes = Encoding.UTF8.GetBytes(stringData);
        SendData(stringBytes);
    }

    protected byte[] StringToBytes(string stringData)
    {
        return Encoding.UTF8.GetBytes(stringData);
    }

    void SenderThread()
    {
        DebugOutput("sending descriptor");
        RegisterWithDescriptor();
        // need to wait for an OK for any send data to proceed
        _sendSync.WaitOne();

        while (true)
        {
            // Wait data ready
            _queueBufferSem.WaitOne();

            // copy to send buffer guarded with lock
            _queueBufferLock.WaitOne();
            _sendNumBytes = _queueNumBytes;
            Array.Copy(_queueBuffer, _sendBuffer, _sendNumBytes);
            _queueBufferLock.ReleaseMutex();
            SendData(_sendBuffer, _sendNumBytes);

            // wait for listener OK signal
            _sendSync.WaitOne();
        }

        return;
    }

    protected virtual void RegisterWithDescriptor()
    {
        return;
    }

    protected virtual byte[] GetFrameBytes()
    {
        return null;
    }

    protected virtual void ApplyControlData()
    {
        return;
    }

    public void UpdateFrameData(byte[] givenBytes = null)
    {
        byte[] frameBytes = null;
        if (givenBytes != null)
        {
            frameBytes = givenBytes;
        }
        else
        {
            frameBytes = GetFrameBytes();
        }

        // Copy to frame buffer
        _queueBufferLock.WaitOne();
        _queueNumBytes = (UInt32)frameBytes.Length;
        Array.Copy(frameBytes, _queueBuffer, _queueNumBytes);
        _queueBufferLock.ReleaseMutex();

        try
        {
            // Signal sender thread new data is ready
            _queueBufferSem.Release();
        }
        catch (SemaphoreFullException semEx)
        {
            DebugOutput("Frame sent to host dropped");
        }
    }

    public void HandlingControlData()
    {
        if (_numControlBytes > 0)
        {
            string result = "";
            try
            {
                ApplyControlData();
                result = "OK";
            }
            catch (Exception e)
            {
                DebugOutput(e.Message);
                result = "FAIL";
            }

            _numControlBytes = 0;
            SendString(result);
        }
    }

    public void Close()
    {
        _connection.Close();
    }
}
