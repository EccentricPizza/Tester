using Microsoft.AspNetCore.Mvc;
using PizzaMVP.DAL.Models;
using PizzaMVP.Services.Interfaces;
using PizzaMVP.API.Models;
using PizzaMVP.DAL.OtherModels;

namespace PizzaMVP.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class LocationsController : ControllerBase
    {
        private readonly ILocationService _locationService;

        public LocationsController(ILocationService locationService)
        {
            _locationService = locationService;
        }

        [HttpGet]
        public async Task<IActionResult> GetActiveLocations()
        {
            var locations = await _locationService.GetActiveLocationsAsync();
            return Ok(locations);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetLocationWithOpeningHours(int id)
        {
            var location = await _locationService.GetLocationWithOpeningHoursAsync(id);
            if (location == null)
                return NotFound();

            return Ok(location);
        }

        [HttpPost("{id}/pickup-times")]
        public async Task<IActionResult> GetPickupTimes(int id, [FromBody] List<CartItemDto> cartItems)
        {
            var pickupTimes = await _locationService.GetAvailablePickupTimesAsync(id, cartItems ?? new List<CartItemDto>());
            return Ok(pickupTimes);
        }
    }
}
