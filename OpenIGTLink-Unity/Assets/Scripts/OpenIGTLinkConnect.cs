using UnityEngine;
using System;
using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Collections;
using System.Threading;
using System.Collections.Generic;
//using System.Runtime.InteropServices;

public class OpenIGTLinkConnect : MonoBehaviour
{

    public int scaleMultiplier = 1000; // Metres to millimetres

    //Set from config.txt, which is located in the project folder when run from the editor
    public string ipString = "127.0.0.1";
    public int port = 18945;
    public GameObject[] GameObjects;
    public int msDelay = 33;

    private float totalTime = 0f;

    //CRC ECMA-182
    private CRC64 crcGenerator;
    private string CRC;
    private string crcPolynomialBinary = "1010000101111000011100001111010111010100111101010001101101001001";
    private ulong crcPolynomial;

    private Socket socket;
    private IPEndPoint remoteEP;

    // ManualResetEvent instances signal completion.
    private static ManualResetEvent connectDone = new ManualResetEvent(false);
    private static ManualResetEvent sendDone = new ManualResetEvent(false);
    private static ManualResetEvent receiveDone = new ManualResetEvent(false);

    // Receive transform queue
    public readonly static Queue<Action> ReceiveTransformQueue = new Queue<Action>();

    private bool connectionStarted = false;

    // Use this for initialization
    void Start()
    {
        // Initialize CRC Generator
        crcGenerator = new CRC64();
        crcPolynomial = Convert.ToUInt64(crcPolynomialBinary, 2);
        crcGenerator.Init(crcPolynomial);
        StartupClient();
    }

    private void StartupClient()
    {
        // Attempt to Connect
        try
        {
            // Establish the remote endpoint for the socket.
            IPAddress ipAddress = IPAddress.Parse(ipString);
            remoteEP = new IPEndPoint(ipAddress, port);

            // Create a TCP/IP  socket.
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Blocking = false;

            try
            {
                // Connect the socket to the remote endpoint. Catch any errors.
                socket.BeginConnect(remoteEP, new AsyncCallback(ConnectCallback), socket);
                connectionStarted = true;

                StartCoroutine(Receive());
                Debug.Log(String.Format("Ready to receive data"));

            }
            catch (Exception e)
            {
                Debug.Log(String.Format("Exception : {0}", e.ToString()));
            }
        }
        catch (Exception e)
        {
            Debug.Log(String.Format(e.ToString()));
        }
    }

    private void ConnectCallback(IAsyncResult ar)
    {
        try
        {
            // Retrieve the socket from the state object.
            Socket client = (Socket)ar.AsyncState;

            // Complete the connection.
            client.EndConnect(ar);

            Debug.Log(String.Format("Socket connected"));

            // Signal that the connection has been made.
            connectDone.Set();
        }
        catch (Exception e)
        {
            Debug.Log(e.ToString());
        }
    }

    // Update is called once per frame
    void Update()
    {

        // Repeat every msDelay millisecond
        if (totalTime * 1000 > msDelay)
        {

            if (connectionStarted)
            {
                // Perform all queued Receive Transforms
                while (ReceiveTransformQueue.Count > 0)
                {

                    ReceiveTransformQueue.Dequeue().Invoke();
                }
            }
            // Reset timer
            totalTime = 0f;
        }
        totalTime = totalTime + Time.deltaTime;
    }

    void OnApplicationQuit()
    {
        // Release the socket.
        socket.Shutdown(SocketShutdown.Both);
        socket.Close();
    }

    IEnumerator Receive()
    {

        while (true)
        {
            //Should execute only once every frame, but add additional delay if neccessary
            //yield return new WaitForSeconds(1.0f);
            yield return null;


            if (socket.Poll(0, SelectMode.SelectRead))
            {
                Receive(socket);
            }
        }
    }

    // -------------------- Receive -------------------- 
    private void Receive(Socket client)
    {

        try
        {
            // Create the state object.
            StateObject state = new StateObject();
            state.workSocket = client;

            // Begin receiving the data from the remote device.
            ReceiveStart(state);
        }
        catch (Exception e)
        {
            Debug.Log(String.Format(e.ToString()));
        }
    }

    private void ReceiveStart(StateObject state)
    {

        Socket client = state.workSocket;
        client.BeginReceive(state.buffer, 0, StateObject.BufferSize, 0, new AsyncCallback(ReceiveCallback), state);
    }

    private void ReceiveCallback(IAsyncResult ar)
    {

        try
        {
            // Retrieve the state object and the client socket 
            // from the asynchronous state object.
            StateObject state = (StateObject)ar.AsyncState;
            Socket client = state.workSocket;
           
            // Read data from the remote device.
            int bytesRead = client.EndReceive(ar);
            Debug.Log(bytesRead);
            // As far as I can tell, Unity will not let the callback occur with 0 bytes to read, so I cannot use a 0 bytes left method to determine ending, must read the data type and size from the Header
            // TODO: Current workaround: adding check for a full buffer of transforms (divisible by 106), this may fail with other data types, must make overflow buffer work as well
            if ((bytesRead > 0))
            {
                
               // Debug.Log(bytesRead);
                // There might be more data, so store the data received so far.
                byte[] readBytes = new Byte[bytesRead];
                Array.Copy(state.buffer, readBytes, bytesRead);
                state.byteList.AddRange(readBytes);
                bool moreToRead = true;

                state.totalBytesRead += bytesRead;

                while (moreToRead)
                {
                    // Read the header and determine data type
                    if (!state.headerRead & state.byteList.Count > 0)
                    {
                        string dataType = Encoding.ASCII.GetString(state.byteList.GetRange(2, 12).ToArray()).Replace("\0", string.Empty);
                        state.name = Encoding.ASCII.GetString(state.byteList.GetRange(14, 20).ToArray()).Replace("\0", string.Empty);
                        byte[] dataSizeBytes = state.byteList.GetRange(42, 8).ToArray();
                        byte[] extHeaderBytes = state.byteList.GetRange(58, 2).ToArray();
                        byte[] metaDataSizeBytes = state.byteList.GetRange(60, 2).ToArray();
                        byte[] Msg_id = state.byteList.GetRange(62, 4).ToArray();

                        if (BitConverter.IsLittleEndian)
                        {
                            Array.Reverse(extHeaderBytes);
                            Array.Reverse(metaDataSizeBytes);
                            Array.Reverse(dataSizeBytes);
                            Array.Reverse(Msg_id);

                        }
                        int ext_Header_size_int = (int)BitConverter.ToUInt16(extHeaderBytes, 0);
                        int MetaData_size = (int)BitConverter.ToUInt16(metaDataSizeBytes, 0);
                        int msgID = (int)BitConverter.ToUInt32(Msg_id, 0);
                        state.dataSize = BitConverter.ToInt32(dataSizeBytes, 0) + 58;
                        /*  Debug.Log("Body Size : " + BitConverter.ToInt32(dataSizeBytes, 0));
                          Debug.Log("extra header size : " + ext_Header_size_int);
                          Debug.Log("Meta data size: " + MetaData_size);
                          Debug.Log("MGd Id : " + msgID);
                          Debug.Log(state.name);*/

                        //Debug.Log(String.Format("Data is of type {0} with name {1} and size {2}", dataType, state.name, state.dataSize));

                        if (dataType.Equals("IMAGE"))
                        {
                            state.dataType = StateObject.DataTypes.IMAGE;
                        }
                        else if (dataType.Equals("TRANSFORM"))
                        {
                            state.dataType = StateObject.DataTypes.TRANSFORM;
                        }
                        else
                        {
                            moreToRead = false;
                            receiveDone.Set();
                            return;
                        }
                        state.headerRead = true;
                    }

                    if (state.totalBytesRead == state.dataSize)
                    {
                        // All the data has arrived; put it in response.
                        if (state.byteList.Count > 1)
                        {
                            // Send off to interpret data based on data type
                            if (state.dataType == StateObject.DataTypes.TRANSFORM)
                            {
                                OpenIGTLinkConnect.ReceiveTransformQueue.Enqueue(() =>
                                {
                                    StartCoroutine(ReceiveTransformMessage(state.byteList.ToArray(), state.name));
                                });
                            }
                        }
                        // Signal that all bytes have been received.
                        moreToRead = false;
                        receiveDone.Set();
                    }
                    else if ((state.totalBytesRead > state.dataSize) & (state.byteList.Count > state.dataSize) & state.dataSize > 0)
                    {
                        // More data than expected has arrived; put it in response and repeat.
                        // Send off to interpret data based on data type
                        if (state.dataType == StateObject.DataTypes.TRANSFORM)
                        {
                            OpenIGTLinkConnect.ReceiveTransformQueue.Enqueue(() =>
                            {
                                try
                                {
                                    //Debug.Log(state.dataSize);
                                    StartCoroutine(ReceiveTransformMessage(state.byteList.GetRange(0, state.dataSize).ToArray(), state.name));
                                }
                                catch (Exception e)
                                {
                                    Debug.Log(String.Format("{0} receiving {1} with total {2}", state.byteList.Count, state.dataSize, state.totalBytesRead));
                                    Debug.Log(String.Format(e.ToString()));
                                }
                            });
                        }
                        state.byteList.RemoveRange(0, state.dataSize);
                        state.totalBytesRead = state.totalBytesRead - state.dataSize;
                        state.dataSize = 0;
                        state.name = "";
                        state.headerRead = false;
                    }
                    else
                    {
                        moreToRead = false;
                        // Get the rest of the data.
                        ReceiveStart(state);
                    }
                }
            }
            else
            {
                receiveDone.Set();
            }
        }
        catch (Exception e)
        {
            receiveDone.Set();
            Debug.Log(String.Format(e.ToString()));
        }
    }

    IEnumerator ReceiveTransformMessage(byte[] data, string transformName)
    {
        // Find Game Objects with Transform Name and determine if they should be updated
        Matrix4x4 CameraToReference = new Matrix4x4();
        Matrix4x4 StylusToReference = new Matrix4x4();
        Matrix4x4 TrackerToReference = new Matrix4x4();

        

        string objectName;

       
        foreach (GameObject gameObject in GameObjects)
        {
            // Could be a bit more efficient
            if (gameObject.name.Length > 20)
            {
                objectName = gameObject.name.Substring(0, 20);
            }
            else
            {
                objectName = gameObject.name;
            }
           
            if (objectName.Equals(transformName) & gameObject.GetComponent<OpenIGTLinkFlag>().ReceiveTransform )
            {
                // Transform Matrix starts from byte 70
                // Extract transform matrix
                byte[] matrixBytes = new byte[4];
                float[] m = new float[12];
                for (int i = 0; i < 12; i++)
                {
                    Buffer.BlockCopy(data, 70 + i * 4, matrixBytes, 0, 4);
                    if (BitConverter.IsLittleEndian)
                    {
                        Array.Reverse(matrixBytes);
                    }

                    m[i] = BitConverter.ToSingle(matrixBytes, 0);
                }
           

                // Slicer units are in millimeters, Unity is in meters, so convert accordingly
                // Definition for Matrix4x4 is extended from SteamVR
                
                StylusToReference.SetRow(0, new Vector4(m[0], m[3], m[6], m[9] / scaleMultiplier  ));
                StylusToReference.SetRow(1, new Vector4(m[1], m[4], m[7], m[10]  / scaleMultiplier));
                StylusToReference.SetRow(2, new Vector4(m[2], m[5], m[8], m[11]  /  scaleMultiplier));
                StylusToReference.SetRow(3, new Vector4(0.0f, 0.0f, 0.0f, 1.0f ));


               /* Matrix4x4 CalibratedMatrix = new Matrix4x4();
                CalibratedMatrix = CameraToReference * Matrix4x4.Inverse(StylusToReference) ;*/

         

                Vector3 translation = StylusToReference.GetColumn(3);
                gameObject.transform.position = new Vector3(-translation.x, translation.y, translation.z);
                Vector3 eulerAngles = StylusToReference.rotation.eulerAngles;
                gameObject.transform.rotation = Quaternion.Euler(-eulerAngles.x, -eulerAngles.y, -eulerAngles.z);
            }
            yield return null;
        }
        // Place this inside the loop if you only want to perform one loop per update cycle
       
    }

}

public class CRC64
{
    private ulong[] _table;

    private ulong CmTab(int index, ulong poly)
    {
        ulong retval = (ulong)index;
        ulong topbit = (ulong)1L << (64 - 1);
        ulong mask = 0xffffffffffffffffUL;

        retval <<= (64 - 8);
        for (int i = 0; i < 8; i++)
        {
            if ((retval & topbit) != 0)
                retval = (retval << 1) ^ poly;
            else
                retval <<= 1;
        }
        return retval & mask;
    }

    private ulong[] GenStdCrcTable(ulong poly)
    {
        ulong[] table = new ulong[256];
        for (var i = 0; i < 256; i++)
            table[i] = CmTab(i, poly);
        return table;
    }

    private ulong TableValue(ulong[] table, byte b, ulong crc)
    {
        return table[((crc >> 56) ^ b) & 0xffUL] ^ (crc << 8);
    }

    public void Init(ulong poly)
    {
        _table = GenStdCrcTable(poly);
    }

    public ulong Compute(byte[] bytes, ulong initial, ulong final)
    {
        ulong current = initial;
        for (var i = 0; i < bytes.Length; i++)
        {
            current = TableValue(_table, bytes[i], current);
        }
        return current ^ final;

    }
 
}

// Receive Object
public class StateObject
{
    // Client socket.
    public Socket workSocket = null;
    // Size of receive buffer.
    public const int BufferSize = 4194304;
    // Receive buffer.
    public byte[] buffer = new byte[BufferSize];
    // Received data string.
    //public StringBuilder sb = new StringBuilder();
    public List<Byte> byteList = new List<Byte>();
    // OpenIGTLink Data Type
    public enum DataTypes {IMAGE = 0, TRANSFORM};
    public DataTypes dataType;
    // Header read or not
    public bool headerRead = false;
    // Data Size read from header
    public int dataSize = -1;
    // Bytes of data read so far
    public int totalBytesRead = 0;
    // Transform Name
    public string name;
}