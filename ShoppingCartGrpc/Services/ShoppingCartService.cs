using AutoMapper;
using Grpc.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShoppingCartGrpc.Data;
using ShoppingCartGrpc.Models;
using ShoppingCartGrpc.Protos;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ShoppingCartGrpc.Services
{
    [Authorize]
    public class ShoppingCartService : ShoppingCartProtoService.ShoppingCartProtoServiceBase
    {
        private readonly ShoppingCartContext _shoppingCartDbContext;
        private readonly DiscountService _discountService;
        private readonly ILogger<ShoppingCartService> _logger;
        private readonly IMapper _mapper;

        public ShoppingCartService(ShoppingCartContext shoppingCartDbContext, DiscountService discountService, ILogger<ShoppingCartService> logger, IMapper mapper)
        {
            _shoppingCartDbContext = shoppingCartDbContext ?? throw new ArgumentNullException(nameof(shoppingCartDbContext));
            _discountService = discountService ?? throw new ArgumentNullException(nameof(discountService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _mapper = mapper ?? throw new ArgumentNullException(nameof(mapper));
        }

        public override async Task<ShoppingCartModel> GetShoppingCart(GetShoppingCartRequest request,
            ServerCallContext context)
        {
            var shoppingCart = await _shoppingCartDbContext.ShoppingCart
                .FirstOrDefaultAsync(s => s.UserName == request.Username);

            if (shoppingCart == null)
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"ShoppingCart with UserName={request.Username}"));
            }

            var shoppingCartModel = _mapper.Map<ShoppingCartModel>(shoppingCart);

            return shoppingCartModel;
        }

        public override async Task<ShoppingCartModel> CreateShoppingCart(ShoppingCartModel request,
            ServerCallContext context)
        {
            var shoppingCart = _mapper.Map<ShoppingCart>(request);

            var isExist = await _shoppingCartDbContext.ShoppingCart.AnyAsync(s => s.UserName == shoppingCart.UserName);

            if (isExist)
            {
                _logger.LogError("Invalid UserName for ShoppingCart creation. UserName : {userName}", shoppingCart.UserName);
                throw new RpcException(new Status(StatusCode.NotFound, $"ShoppingCart with UserName={shoppingCart.UserName}"));
            }

            _shoppingCartDbContext.ShoppingCart.Add(shoppingCart);
            await _shoppingCartDbContext.SaveChangesAsync();

            _logger.LogInformation("ShoppingCart is successfully created.UserName : {userName}", shoppingCart.UserName);

            var shoppingCartModel = _mapper.Map<ShoppingCartModel>(shoppingCart);

            return shoppingCartModel;
        }

        [AllowAnonymous]
        public override async Task<RemoveItemIntoShoppingCartResponse> RemoveItemIntoShoppingCart(RemoveItemIntoShoppingCartRequest request,
            ServerCallContext context)
        {
            var shoppingCart = await _shoppingCartDbContext.ShoppingCart.FirstOrDefaultAsync(s => s.UserName == request.Username);
            if (shoppingCart == null)
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"ShoppingCart with UserName={shoppingCart.UserName}"));
            }

            var removeCartItem = shoppingCart.Items.FirstOrDefault(i => i.ProductId == request.RemoveCartItem.ProductId);
            if (removeCartItem == null)
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"CartItem with ProductId={request.RemoveCartItem.ProductId}"));
            }

            shoppingCart.Items.Remove(removeCartItem);
            var removeCount = await _shoppingCartDbContext.SaveChangesAsync();

            var response = new RemoveItemIntoShoppingCartResponse
            {
                Success = removeCount > 0
            };

            return response;
        }

        [AllowAnonymous]
        public override async Task<AddItemIntoShoppingCartResponse> AddItemIntoShoppingCart(
            IAsyncStreamReader<AddItemIntoShoppingCartRequest> requestStream,
            ServerCallContext context)
        {
            while (await requestStream.MoveNext())
            {
                var shoppingCart = await _shoppingCartDbContext.ShoppingCart
                .FirstOrDefaultAsync(s => s.UserName == requestStream.Current.Username);
                if (shoppingCart == null)
                {
                    throw new RpcException(new Status(StatusCode.NotFound,
                        $"ShoppingCart with UserName={shoppingCart.UserName}"));
                }

                var newAddedCartItem = _mapper.Map<ShoppingCartItem>(requestStream.Current.NewCartItem);
                var cartItem = shoppingCart.Items.FirstOrDefault(i => i.ProductId == newAddedCartItem.ProductId);
                if (cartItem != null)
                {
                    cartItem.Quantity++;
                }
                else
                {
                    //grpc call discount -- check discount and calculate last price
                    var discount = await _discountService.GetDiscount(requestStream.Current.DiscountCode);
                    newAddedCartItem.Price -= discount.Amount;

                    shoppingCart.Items.Add(newAddedCartItem);
                }
            }

            var insertCount = await _shoppingCartDbContext.SaveChangesAsync();

            var response = new AddItemIntoShoppingCartResponse
            {
                Success = insertCount > 0,
                InsertCount = insertCount
            };

            return response;
        }
    }
}
