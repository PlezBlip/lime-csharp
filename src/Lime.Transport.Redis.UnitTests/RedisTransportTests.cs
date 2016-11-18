﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lime.Protocol;
using Lime.Protocol.Network;
using Lime.Protocol.Serialization;
using Lime.Protocol.Serialization.Newtonsoft;
using Lime.Protocol.UnitTests;
using Moq;
using NUnit.Framework;
using Shouldly;
using StackExchange.Redis;

namespace Lime.Transport.Redis.UnitTests
{
    [TestFixture]
    public class RedisTransportTests
    {
        public static string AssemblyDirectory
        {
            get
            {
                string codeBase = Assembly.GetExecutingAssembly().CodeBase;
                UriBuilder uri = new UriBuilder(codeBase);
                string path = Uri.UnescapeDataString(uri.Path);
                return Path.GetDirectoryName(path);
            }
        }

        public Uri ListenerUri { get; private set; }

        public IEnvelopeSerializer EnvelopeSerializer { get; private set; }        

        public RedisTransportListener Listener { get; private set; }

        public Mock<ITraceWriter> TraceWriter { get; private set; }

        public CancellationToken CancellationToken { get; private set; }        

        public ITransport ServerTransport { get; private set; }

        public Process RedisProccess { get; private set; }

        public Session EstablishedSession { get; private set; }

        public RedisTransport GetTarget()
        {
            return new RedisTransport(
                ListenerUri,
                new JsonNetSerializer());
        }

        public async Task<RedisTransport> GetTargetAndOpenAsync()
        {
            var transport = GetTarget();
            await transport.OpenAsync(ListenerUri, CancellationToken);
            return transport;
        }

        public async Task<RedisTransport> GetTargetAndEstablish()
        {
            var transport = await GetTargetAndOpenAsync();            
            await transport.SendAsync(new Session { State = SessionState.New }, CancellationToken);
            ServerTransport = await Listener.AcceptTransportAsync(CancellationToken);
            await ServerTransport.ReceiveAsync(CancellationToken);
            EstablishedSession = Dummy.CreateSession(SessionState.Established);
            await ServerTransport.SendAsync(EstablishedSession, CancellationToken);
            await transport.ReceiveAsync(CancellationToken);
            return transport;
        }

        [OneTimeSetUp]
        public void OneTimeSetup()
        {
            var redisProcesses = Process.GetProcessesByName("redis-server");
            if (!redisProcesses.Any())
            {                
                var executablePath = Path.Combine(AssemblyDirectory, @"..\..\..\packages\redis-64.3.0.503\tools\redis-server.exe");

                if (!File.Exists(executablePath))
                {
                    throw new Exception($"Could not find the Redis executable at '{executablePath}'");
                }

                var processStartInfo = new ProcessStartInfo()
                {
                    FileName = executablePath,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                RedisProccess = Process.Start(processStartInfo);
            }
        }

        [OneTimeTearDown]
        public void OneTimeTearDown()
        {
            RedisProccess?.Close();
        }

        [SetUp]
        public async Task SetUpAsync()
        {
            ListenerUri = new Uri("redis://localhost");
            EnvelopeSerializer = new JsonNetSerializer();
            TraceWriter = new Mock<ITraceWriter>();
            Listener = new RedisTransportListener(ListenerUri, EnvelopeSerializer, TraceWriter.Object);
            await Listener.StartAsync();
            CancellationToken = TimeSpan.FromSeconds(30).ToCancellationToken();
        }


        [TearDown]
        public async Task TearDownAsync()
        {
            await Listener.StopAsync();
            Listener.Dispose();
        }

        [Test]
        public async Task SendAsync_NewSessionEnvelope_ServerShouldReceive()
        {
            // Arrange
            var session = Dummy.CreateSession(SessionState.New);
            session.Id = null;
            var target = await GetTargetAndOpenAsync();

            // Act
            await target.SendAsync(session, CancellationToken);

            // Assert
            var serverTransport = await Listener.AcceptTransportAsync(CancellationToken);
            var receivedEnvelope = await serverTransport.ReceiveAsync(CancellationToken);
            var receivedSession = receivedEnvelope.ShouldBeOfType<Session>();
            receivedSession.State.ShouldBe(SessionState.New);
        }

        [Test]
        public async Task SendAsync_FinishingSessionEnvelope_ServerShouldReceive()
        {
            // Arrange            
            var target = await GetTargetAndEstablish();
            var session = Dummy.CreateSession(SessionState.Finishing);
            session.Id = EstablishedSession.Id;

            // Act
            await target.SendAsync(session, CancellationToken);

            // Assert
            var receivedEnvelope = await ServerTransport.ReceiveAsync(CancellationToken);
            var receivedSession = receivedEnvelope.ShouldBeOfType<Session>();
            receivedSession.State.ShouldBe(SessionState.Finishing);
        }

        [Test]
        public async Task SendAsync_MessageEnvelope_ServerShouldReceive()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreateTextContent());

        }
    }
}