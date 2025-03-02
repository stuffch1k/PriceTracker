﻿using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PriceTracker.Domain.Entities;
using PriceTracker.Domain.Telegram;
using PriceTracker.Infrastructure.Context;
using PriceTracker.Parser;
using PriceTracker.Services.Parser.Factory;
using PriceTracker.Services.User;
using Quartz;

namespace PriceTracker.BackgroundJob;

public class ParsingBackgroundJob : IJob
{
    private readonly ILogger<ParsingBackgroundJob> _logger;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IParserFactory _parserFactory;
    private readonly ITelegramClient _client;
    private readonly IUserService _userService;
    private const double _eps = 1e-10;

    public ParsingBackgroundJob(ILogger<ParsingBackgroundJob> logger,
        IDbContextFactory<AppDbContext> factory,
        IServiceProvider provider, 
        ITelegramClient client, 
        IUserService userService, IEnumerable<IParser> parsers)
    {
        _logger = logger;
        _factory = factory;
        _client = client;
        _userService = userService;
        _parserFactory = provider.CreateScope().ServiceProvider.GetService<IParserFactory>()!;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        _logger.LogInformation("background working started at {StartTime}", DateTime.Now);
        await using var dbContext = await _factory.CreateDbContextAsync();
        var users = dbContext.Users
            .Include(x => x.Products)
            .ThenInclude(x => x.Prices.OrderByDescending(p => p.CreateDate).Take(1));
        
        foreach (var user in users)
        {
            var products = user.Products.ToList();
            foreach (var product in products)
            {
                var parser = _parserFactory.CreateParser(product.MarketPlaceName);
                
                if (parser is null)
                    continue;

                var price = product.Prices.Last();
                
                _logger.LogInformation("The last price creted at: {CreatedAt} and has current price = {Price}, " +
                                       "dscounted price = {DiscountedPrice}", price.CreateDate, price.CurrentPrice, price.DiscountedPrice);
                
                var result = await parser.ParseAsync(product.Link);

                if (result.Title is not null && (Math.Abs(price.CurrentPrice - (double)result?.Price!) > _eps
                                                 || Math.Abs(price.DiscountedPrice - (double)result?.CardPrice!) > _eps))
                {
                    _logger.LogInformation("The product name is {Title} has price: {Price} and discounted price: {DiscountedPrice}",
                        result.Title, result.Price, result.CardPrice);
                    product.Prices.Add(new Price(result.Price ?? 0.0, result.CardPrice ?? 0.0));
                    var message = $"\ud83d\udd14 Уведомление об изменении цены!\n" +
                                  $"\ud83d\udc49 Название товара: *{result.Title}*\n" +
                                  $"\n" +
                                  $"\ud83d\udcb0 Цена без скидки: *{result.Price}* \u20bd \n" +
                                  $"\ud83d\udcb3 Цена по скидке/карте:  *{result.CardPrice}* \u20bd \n" +
                                  $"\n" +
                                  $"\ud83d\udd17 [Ссылка на товар]({product.Link})";
                    await _client.SendPriceChangingNotification(user.ChatId, message);
                }
                
                // if (result.Title is not null)
                // {
                //     _logger.LogInformation("The product name is {Title} has price: {Price} and discounted price: {DiscountedPrice}",
                //         result.Title, result.Price, result.CardPrice);
                //     product.Prices.Add(new Price(result.Price ?? 0.0, result.CardPrice ?? 0.0));
                //     var message = $"Название товара *{result.Title}*\n" +
                //                   $"Цена без скидки: *{result.Price}*\n" +
                //                   $"Цена со скидкой (по скидочной карте) *{result.CardPrice}*";
                //
                //     await _client.SendPriceChangingNotification(user.ChatId, message);
                // }

                dbContext.Products.Update(product);
            }
        }
        await dbContext.SaveChangesAsync();
        
        _logger.LogInformation("background working ended at {EndTime}", DateTime.Now);
    }
}