using System;
using Unity.Collections;
using Unity.Jobs;

namespace Unity.Networking.Transport
{
    public struct LocalNetworkDriver : INetworkDriver
    {
        private GenericNetworkDriver<IPCSocket, DefaultPipelineStageCollection> m_genericDriver;

        public struct Concurrent
        {
            private GenericNetworkDriver<IPCSocket, DefaultPipelineStageCollection>.Concurrent m_genericConcurrent;

            internal Concurrent(GenericNetworkDriver<IPCSocket, DefaultPipelineStageCollection>.Concurrent genericConcurrent)
            {
                m_genericConcurrent = genericConcurrent;
            }
            public NetworkEvent.Type PopEventForConnection(NetworkConnection connectionId, out DataStreamReader slice)
            {
                return m_genericConcurrent.PopEventForConnection(connectionId, out slice);
            }

            public int Send(NetworkPipeline pipe, NetworkConnection id, DataStreamWriter strm)
            {
                return m_genericConcurrent.Send(pipe, id, strm);
            }

            public int Send(NetworkPipeline pipe, NetworkConnection id, IntPtr data, int len)
            {
                return m_genericConcurrent.Send(pipe, id, data, len);
            }

            public NetworkConnection.State GetConnectionState(NetworkConnection id)
            {
                return m_genericConcurrent.GetConnectionState(id);
            }
        }

        public Concurrent ToConcurrent()
        {
            return new Concurrent(m_genericDriver.ToConcurrent());
        }

        public LocalNetworkDriver(params INetworkParameter[] param)
        {
            m_genericDriver = new GenericNetworkDriver<IPCSocket, DefaultPipelineStageCollection>(param);
        }
        public bool IsCreated => m_genericDriver.IsCreated;
        public void Dispose()
        {
            m_genericDriver.Dispose();
        }

        public JobHandle ScheduleUpdate(JobHandle dep = default(JobHandle))
        {
            return m_genericDriver.ScheduleUpdate(dep);
        }

        public int ReceiveErrorCode => m_genericDriver.ReceiveErrorCode;

        public int Bind(NetworkEndPoint endpoint)
        {
            return m_genericDriver.Bind(endpoint);
        }

        public int Listen()
        {
            return m_genericDriver.Listen();
        }

        public bool Listening => m_genericDriver.Listening;

        public NetworkConnection Accept()
        {
            return m_genericDriver.Accept();
        }

        public NetworkConnection Connect(NetworkEndPoint endpoint)
        {
            return m_genericDriver.Connect(endpoint);
        }

        public int Disconnect(NetworkConnection con)
        {
            return m_genericDriver.Disconnect(con);
        }

        public NetworkConnection.State GetConnectionState(NetworkConnection con)
        {
            return m_genericDriver.GetConnectionState(con);
        }

        public void GetPipelineBuffers(Type pipelineType, NetworkConnection connection,
            ref NativeSlice<byte> readProcessingBuffer, ref NativeSlice<byte> writeProcessingBuffer,
            ref NativeSlice<byte> sharedBuffer)
        {
            m_genericDriver.GetPipelineBuffers(pipelineType, connection, ref readProcessingBuffer,
                ref writeProcessingBuffer, ref sharedBuffer);
        }

        public void GetPipelineBuffers(NetworkPipeline pipeline, int stageId, NetworkConnection connection, ref NativeSlice<byte> readProcessingBuffer, ref NativeSlice<byte> writeProcessingBuffer, ref NativeSlice<byte> sharedBuffer)
        {
            m_genericDriver.GetPipelineBuffers(pipeline, stageId, connection, ref readProcessingBuffer, ref writeProcessingBuffer, ref sharedBuffer);
        }

        public NetworkEndPoint RemoteEndPoint(NetworkConnection con)
        {
            return m_genericDriver.RemoteEndPoint(con);
        }

        public NetworkEndPoint LocalEndPoint()
        {
            return m_genericDriver.LocalEndPoint();
        }

        public NetworkPipeline CreatePipeline(params Type[] stages)
        {
            return m_genericDriver.CreatePipeline(stages);
        }

        public int Send(NetworkPipeline pipe, NetworkConnection con, DataStreamWriter strm)
        {
            return m_genericDriver.Send(pipe, con, strm);
        }

        public int Send(NetworkPipeline pipe, NetworkConnection con, IntPtr data, int len)
        {
            return m_genericDriver.Send(pipe, con, data, len);
        }

        public NetworkEvent.Type PopEvent(out NetworkConnection con, out DataStreamReader bs)
        {
            return m_genericDriver.PopEvent(out con, out bs);
        }

        public NetworkEvent.Type PopEventForConnection(NetworkConnection con, out DataStreamReader bs)
        {
            return m_genericDriver.PopEventForConnection(con, out bs);
        }
    }
}