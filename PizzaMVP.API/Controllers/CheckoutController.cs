using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PizzaMVP.API.Models;
using PizzaMVP.DAL.Contexts;
using PizzaMVP.DAL.Models;
using PizzaMVP.DAL.Enums;
using PizzaMVP.Services.Interfaces;
using Stripe;
using Stripe.Checkout;

namespace PizzaMVP.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CheckoutController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly PizzaDbContext _context;
        private readonly IEmailService _emailService;

        public CheckoutController(IConfiguration configuration, PizzaDbContext context, IEmailService emailService)
        {
            _configuration = configuration;
            _context = context;
            _emailService = emailService;
            StripeConfiguration.ApiKey = _configuration["Stripe:SecretKey"];
        }

        [HttpPost("create-session")]
        public async Task<IActionResult> CreateCheckoutSession([FromBody] CheckoutRequest request)
        {
            try
            {
                var lineItems = request.Items.Select(item => new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        UnitAmount = (long)(item.UnitPrice * 100), // Convert to pence
                        Currency = "gbp",
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = item.MenuItemName,
                            Description = GetItemDescription(item),
                        },
                    },
                    Quantity = item.Quantity,
                }).ToList();

                var options = new SessionCreateOptions
                {
                    PaymentMethodTypes = new List<string> { "card" },
                    LineItems = lineItems,
                    Mode = "payment",
                    SuccessUrl = "http://localhost:5173/checkout/success?session_id={CHECKOUT_SESSION_ID}",
                    CancelUrl = "http://localhost:5173/checkout/cancel",
                    CustomerCreation = "always", // Creates customer record - email is mandatory
                    PhoneNumberCollection = new SessionPhoneNumberCollectionOptions
                    {
                        Enabled = true, // Phone number is mandatory when enabled
                    },
                    BillingAddressCollection = "required", // Name is mandatory in billing address
                };

                var service = new SessionService();
                var session = await service.CreateAsync(options);

                return Ok(new { url = session.Url });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpGet("session/{sessionId}")]
        public async Task<IActionResult> GetSession(string sessionId)
        {
            try
            {
                var service = new SessionService();
                var session = await service.GetAsync(sessionId);

                if (session == null)
                {
                    return NotFound("Session not found");
                }

                var response = new
                {
                    SessionId = session.Id,
                    CustomerName = session.CustomerDetails?.Name ?? "",
                    CustomerEmail = session.CustomerDetails?.Email ?? "",
                    CustomerPhone = session.CustomerDetails?.Phone ?? "",
                    PaymentStatus = session.PaymentStatus,
                    TotalAmount = (decimal)session.AmountTotal / 100 // Convert from pence to pounds
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        [HttpPost("save-order")]
        public async Task<IActionResult> SaveOrder([FromBody] SaveOrderRequest request)
        {
            try
            {
                var order = new Order
                {
                    CustomerName = request.CustomerName,
                    PhoneNumber = request.CustomerPhone,
                    Email = request.CustomerEmail,
                    LocationId = request.LocationId,
                    OrderTime = DateTime.Now,
                    Status = OrderStatus.Received,
                    PickupTime = request.PickupTime,
                    StripeSessionId = request.SessionId,
                    TotalPrice = request.TotalAmount,
                };

                _context.Orders.Add(order);
                await _context.SaveChangesAsync();

                // Add order items
                foreach (var cartItem in request.CartItems)
                {
                    var orderItem = new OrderItem
                    {
                        OrderId = order.Id,
                        MenuItemId = cartItem.MenuItemId,
                        Quantity = cartItem.Quantity,
                        BasePrice = cartItem.BasePrice,
                        TotalPrice = cartItem.UnitPrice * cartItem.Quantity
                    };

                    _context.OrderItems.Add(orderItem);
                    await _context.SaveChangesAsync();

                    // Add ingredient modifications
                    foreach (var modification in cartItem.Modifications)
                    {
                        var orderItemIngredient = new OrderItemIngredient
                        {
                            OrderItemId = orderItem.Id,
                            IngredientId = modification.IngredientId,
                            Price = modification.Price,
                            Extra = modification.Extra,
                            Removed = modification.Removed
                        };

                        _context.OrderItemIngredients.Add(orderItemIngredient);
                    }
                }

                await _context.SaveChangesAsync();

                // Load the complete order with related data for email
                var completeOrder = await _context.Orders
                    .Include(o => o.OrderItems)
                        .ThenInclude(oi => oi.MenuItem)
                    .Include(o => o.OrderItems)
                        .ThenInclude(oi => oi.OrderItemIngredients)
                            .ThenInclude(oii => oii.Ingredient)
                    .FirstOrDefaultAsync(o => o.Id == order.Id);

                if (completeOrder != null)
                {
                    // Send confirmation email (don't fail the order if email fails)
                    try
                    {
                        await _emailService.SendOrderConfirmationAsync(completeOrder, completeOrder.OrderItems.ToList());
                    }
                    catch (Exception emailEx)
                    {
                        // Log email failure but don't fail the order
                        Console.WriteLine($"Email sending failed: {emailEx.Message}");
                    }
                }

                return Ok(new { orderId = order.Id, message = "Order saved successfully" });
            }
            catch (Exception ex)
            {
                return BadRequest(new { error = ex.Message });
            }
        }

        private static string GetItemDescription(CheckoutItem item)
        {
            if (!item.Modifications.Any())
                return item.MenuItemDescription ?? string.Empty;

            var modifications = item.Modifications
                .Where(m => !string.IsNullOrEmpty(m.IngredientName))
                .Select(m => m.Extra ? $"+{m.IngredientName}" : $"-{m.IngredientName}")
                .ToList();

            var baseDescription = item.MenuItemDescription ?? string.Empty;
            return modifications.Any() 
                ? $"{baseDescription} ({string.Join(", ", modifications)})"
                : baseDescription;
        }
    }
}
