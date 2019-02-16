using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AElf.Common;
using AElf.Kernel;
using AElf.Kernel.Account;
using AElf.Kernel.Services;
using AElf.OS.Network;
using AElf.OS.Network.Events;
using AElf.OS.Network.Grpc;
using AElf.OS.Network.Temp;
using AElf.Synchronization.Tests;
using Microsoft.Extensions.Options;
using Moq;
using Volo.Abp.EventBus.Local;
using Xunit;
using Xunit.Abstractions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace AElf.OS.Tests.Network
{
    public class GrpcNetworkManagerTests : OSTestBase
    {
        private readonly ITestOutputHelper _testOutputHelper;
        private readonly IOptionsSnapshot<ChainOptions> _optionsMock;

        public GrpcNetworkManagerTests(ITestOutputHelper testOutputHelper)
        {
            _testOutputHelper = testOutputHelper;
            
            var optionsMock = new Mock<IOptionsSnapshot<ChainOptions>>();
            optionsMock.Setup(m => m.Value).Returns(new ChainOptions { ChainId = ChainHelpers.DumpBase58(ChainHelpers.GetRandomChainId()) });
            _optionsMock = optionsMock.Object;
        }

        private (GrpcNetworkServer, IPeerPool) BuildNetManager(NetworkOptions networkOptions, Action<object> eventCallBack = null, List<Block> blockList = null)
        {
            var optionsMock = new Mock<IOptionsSnapshot<NetworkOptions>>();
            optionsMock.Setup(m => m.Value).Returns(networkOptions);
            
            var mockLocalEventBus = new Mock<ILocalEventBus>();
            
            // Catch all events on the bus
            if (eventCallBack != null)
            {
                mockLocalEventBus
                    .Setup(m => m.PublishAsync(It.IsAny<object>()))
                    .Returns<object>(t => Task.CompletedTask)
                    .Callback<object>(m => eventCallBack(m));
            }

            var mockBlockService = new Mock<IFullBlockchainService>();
            if (blockList != null)
            {
                mockBlockService.Setup(bs => bs.GetBlockByHashAsync(It.IsAny<int>(), It.IsAny<Hash>()))
                    .Returns<int, Hash>((chainId, h) => Task.FromResult(blockList.FirstOrDefault(bl => bl.GetHash() == h)));
                
                mockBlockService.Setup(bs => bs.GetBlockByHeightAsync(It.IsAny<int>(), It.IsAny<ulong>()))
                    .Returns<int, ulong>((chainId, h) => Task.FromResult(blockList.FirstOrDefault(bl => bl.Height == h)));
            }
            
            var mockBlockChainService = new Mock<IFullBlockchainService>();
            mockBlockChainService.Setup(m => m.GetBestChainLastBlock(It.IsAny<int>()))
                .Returns(Task.FromResult(new BlockHeader()));

            GrpcPeerPool grpcPeerPool = new GrpcPeerPool(_optionsMock, optionsMock.Object, NetMockHelpers.MockAccountService().Object, mockBlockService.Object);
            GrpcServerService serverService = new GrpcServerService(_optionsMock, grpcPeerPool, mockBlockService.Object);
            serverService.EventBus = mockLocalEventBus.Object;
            
            GrpcNetworkServer netServer = new GrpcNetworkServer(optionsMock.Object, serverService, grpcPeerPool);
            netServer.EventBus = mockLocalEventBus.Object;

            return (netServer, grpcPeerPool);
        }

        [Fact]
        private async Task RequestBlockTest()
        {
            var genesis = ChainGenerationHelpers.GetGenesisBlock();

            var m1 = BuildNetManager(new NetworkOptions { ListeningPort = 6800 },
            null, 
            new List<Block> { (Block) genesis });
            
            var m2 = BuildNetManager(new NetworkOptions
            {
                BootNodes = new List<string> {"127.0.0.1:6800"},
                ListeningPort = 6801
            });
            
            var m3 = BuildNetManager(new NetworkOptions
            {
                BootNodes = new List<string> {"127.0.0.1:6801", "127.0.0.1:6800"},
                ListeningPort = 6802
            });
            
            await m1.Item1.StartAsync();
            await m2.Item1.StartAsync();
            await m3.Item1.StartAsync();

            var service1 = new GrpcNetworkService(m1.Item2);
            var service2 = new GrpcNetworkService(m2.Item2);
            var service3 = new GrpcNetworkService(m3.Item2);

            IBlock b = await service2.GetBlockByHash(genesis.GetHash());
            IBlock bbh = await service3.GetBlockByHeight(genesis.Height);

            await m1.Item1.StopAsync();
            await m2.Item1.StopAsync();
            
            Assert.NotNull(b);
            Assert.NotNull(bbh);

            await m3.Item1.StopAsync();
        }
        
        [Fact]
        private async Task Announcement_EventTest()
        {
            List<AnnoucementReceivedEventData> receivedEventDatas = new List<AnnoucementReceivedEventData>(); 

            void TransferEventCallbackAction(object eventData)
            {
                // todo use event bus
                try
                {
                    if (eventData is AnnoucementReceivedEventData data)
                    {
                        receivedEventDatas.Add(data);
                    }
                }
                catch (Exception e)
                {
                    _testOutputHelper.WriteLine(e.ToString());
                }
            }

            var m1 = BuildNetManager(new NetworkOptions { ListeningPort = 6800 }, TransferEventCallbackAction);
            
            var m2 = BuildNetManager(new NetworkOptions
            {
                BootNodes = new List<string> {"127.0.0.1:6800"},
                ListeningPort = 6801
            });
            
            await m1.Item1.StartAsync();
            await m2.Item1.StartAsync();
            
            var genesis = (Block) ChainGenerationHelpers.GetGenesisBlock();

            var servicem2 = new GrpcNetworkService(m2.Item2);
            await servicem2.BroadcastAnnounce(genesis.Header);
            
            await m1.Item1.StopAsync();
            await m2.Item1.StopAsync();
            
            Assert.True(receivedEventDatas.Count == 1);
            Assert.True(receivedEventDatas.First().Header.GetHash() == genesis.GetHash());
        }
        
        [Fact]
        private async Task Transaction_EventTest()
        {
            List<TxReceivedEventData> receivedEventDatas = new List<TxReceivedEventData>();

            void TransferEventCallbackAction(object eventData)
            {
                // todo use event bus
                try
                {
                    if (eventData is TxReceivedEventData data)
                    {
                        receivedEventDatas.Add(data);
                    }
                }
                catch (Exception e)
                {
                    _testOutputHelper.WriteLine(e.ToString());
                }
            }

            var m1 = BuildNetManager(new NetworkOptions { ListeningPort = 6800 }, TransferEventCallbackAction);
            
            var m2 = BuildNetManager(new NetworkOptions
            {
                BootNodes = new List<string> {"127.0.0.1:6800"},
                ListeningPort = 6801
            });
            
            await m1.Item1.StartAsync();
            await m2.Item1.StartAsync();
            
            var genesis = ChainGenerationHelpers.GetGenesisBlock();

            var servicem2 = new GrpcNetworkService(m2.Item2);
            await servicem2.BroadcastTransaction(new Transaction());
            
            await m1.Item1.StopAsync();
            await m2.Item1.StopAsync();
            
            Assert.True(receivedEventDatas.Count == 1);
        }
        
        private async Task Announcement_Request_Test()
        {
            List<AnnoucementReceivedEventData> receivedEventDatas = new List<AnnoucementReceivedEventData>();

            void TransferEventCallbackAction(object eventData)
            {
                try
                {
                    if (eventData is AnnoucementReceivedEventData data)
                    {
                        receivedEventDatas.Add(data);
                    }
                }
                catch (Exception e)
                {
                    _testOutputHelper.WriteLine(e.ToString());
                }
            }

            var m1 = BuildNetManager(new NetworkOptions { ListeningPort = 6800 }, TransferEventCallbackAction);
            
            var m2 = BuildNetManager(new NetworkOptions
            {
                BootNodes = new List<string> {"127.0.0.1:6800"},
                ListeningPort = 6801
            });
            
            await m1.Item1.StartAsync();
            await m2.Item1.StartAsync();
            
            var genesis = (Block) ChainGenerationHelpers.GetGenesisBlock();

            var servicem2 = new GrpcNetworkService(m2.Item2);
            await servicem2.BroadcastAnnounce(genesis.Header);
            
            await m1.Item1.StopAsync();
            await m2.Item1.StopAsync();
            
            Assert.True(receivedEventDatas.Count == 1);
            Assert.True(receivedEventDatas.First().Header.GetHash() == genesis.GetHash());
        }
    }
}