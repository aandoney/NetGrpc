using Grpc.Core;
using Grpc.Net.Client;
using IdentityModel.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductGrpc.Protos;
using ShoppingCartGrpc.Protos;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace ShoppingCartWorkerService
{
    public class Worker : BackgroundService
    {
        private readonly ILogger<Worker> _logger;
        private readonly IConfiguration _config;

        public Worker(ILogger<Worker> logger, IConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);

                using var scChannel = GrpcChannel.ForAddress(
                    _config.GetValue<string>("WorkerService:ShoppingCartServerUrl"));
                var scClient = new ShoppingCartProtoService.ShoppingCartProtoServiceClient(scChannel);

                // Get Token from IS4
                var token = await GetTokenFromIS4();

                // create shopping cart
                var scModel = await GetOrCreateShoppingCartAsync(scClient, token);

                //retrieve products with server stream
                using var scClientStream = scClient.AddItemIntoShoppingCart();

                using var productChannel = GrpcChannel.ForAddress(_config.GetValue<string>("WorkerService:ProductServerUrl"));
                var productClient = new ProductProtoService.ProductProtoServiceClient(productChannel);

                _logger.LogInformation("GetAllProducts started..");
                using var clientData = productClient.GetAllProducts(new GetAllProductsRequest());
                await foreach (var responseData in clientData.ResponseStream.ReadAllAsync())
                {
                    _logger.LogInformation("GetAllProducts Stream Response: {responseData}", responseData);

                    //add sc items into SC client stream
                    var addNewScItem = new AddItemIntoShoppingCartRequest
                    {
                        Username = _config.GetValue<string>("WorkerService:UserName"),
                        DiscountCode = "CODE_100",
                        NewCartItem = new ShoppingCartItemModel
                        {
                            ProductId = responseData.ProductId,
                            Productname = responseData.Name,
                            Price = responseData.Price,
                            Color = "Black",
                            Quantity = 1
                        }
                    };

                    await scClientStream.RequestStream.WriteAsync(addNewScItem);
                    _logger.LogInformation("ShoppingCart Client Stream Added new item : {addNewScItem}", addNewScItem);
                }

                await scClientStream.RequestStream.CompleteAsync();

                var addItemIntoShoppingCartResponse = await scClientStream;
                _logger.LogInformation("AddItemIntoShoppingCart Client Stream response: {addItemIntoShoppingCarResponse}", addItemIntoShoppingCartResponse);

                await Task.Delay(_config.GetValue<int>("WorkerService:TaskInterval"), stoppingToken);
            }
        }

        private async Task<string> GetTokenFromIS4()
        {
            var client = new HttpClient();
            var disco = await client.GetDiscoveryDocumentAsync(_config.GetValue<string>("WorkerService:IdentityServerUrl"));
            if (disco.IsError)
            {
                _logger.LogError(disco.Error);
                return string.Empty;
            }

            //request token
            var tokenResponse = await client.RequestClientCredentialsTokenAsync(
                new ClientCredentialsTokenRequest
                {
                    Address = disco.TokenEndpoint,
                    ClientId = "ShoppingCartClient",
                    ClientSecret = "secret",
                    Scope = "ShoppingCartAPI"
                }
            );

            if (tokenResponse.IsError)
            {
                _logger.LogError(tokenResponse.Error);
                return string.Empty;
            }

            return tokenResponse.AccessToken;
        }

        private async Task<ShoppingCartModel> GetOrCreateShoppingCartAsync(
            ShoppingCartProtoService.ShoppingCartProtoServiceClient scClient, string token)
        {
            ShoppingCartModel shoppingCartModel;

            try
            {
                _logger.LogInformation("GetShoppingCartAsync started..");

                var headers = new Metadata();
                headers.Add("Authorization", $"Bearer {token}");

                shoppingCartModel = await scClient.GetShoppingCartAsync(
                    new GetShoppingCartRequest { Username = _config.GetValue<string>("WorkerService:UserName") },
                    headers);
                _logger.LogInformation("GetShoppingCartAsync Response: {shoppingCartModel}", shoppingCartModel);
            }
            catch (RpcException exception)
            {
                if (exception.StatusCode == StatusCode.NotFound)
                {
                    shoppingCartModel = await scClient.CreateShoppingCartAsync(
                        new ShoppingCartModel { Username = _config.GetValue<string>("WorkerService:UserName") });
                    _logger.LogInformation("CreateShoppingCartAsync Response: {shoppingCartModel}", shoppingCartModel);
                }
                else
                {
                    throw exception;
                }
            }

            return shoppingCartModel;
        }
    }
}
