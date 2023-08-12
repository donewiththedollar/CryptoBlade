﻿using Bert.RateLimiters;
using Bybit.Net.Interfaces.Clients;
using CryptoBlade.Configuration;
using CryptoBlade.Exchanges;
using CryptoBlade.Strategies;
using CryptoBlade.Strategies.Common;
using CryptoBlade.Strategies.Wallet;
using Microsoft.Extensions.Options;

namespace CryptoBlade.Services
{
    public class DynamicTradingStrategyManager : TradeStrategyManagerBase
    {
        private readonly ILogger<DynamicTradingStrategyManager> m_logger;
        private readonly IOptions<TradingBotOptions> m_options;

        public DynamicTradingStrategyManager(IOptions<TradingBotOptions> options, 
            ILogger<DynamicTradingStrategyManager> logger, 
            ITradingStrategyFactory strategyFactory, 
            ICbFuturesRestClient restClient, 
            IBybitSocketClient bybitSocketClient, 
            IWalletManager walletManager) 
            : base(options, logger, strategyFactory, restClient, bybitSocketClient, walletManager)
        {
            m_options = options;
            m_logger = logger;
        }

        protected override async Task StrategyExecutionAsync(CancellationToken cancel)
        {
            try
            {
                DynamicBotCount dynamicBotCount = m_options.Value.DynamicBotCount;
                int expectedUpdates = Strategies.Count;
                RollingWindowThrottler strategyShortThrottler = new RollingWindowThrottler(m_options.Value.DynamicBotCount.MaxDynamicStrategyOpenPerStep, 
                    dynamicBotCount.Step);
                RollingWindowThrottler strategyLongThrottler = new RollingWindowThrottler(m_options.Value.DynamicBotCount.MaxDynamicStrategyOpenPerStep,
                    dynamicBotCount.Step);
                while (!cancel.IsCancellationRequested)
                {
                    try
                    {
                        await StrategyExecutionChannel.Reader.WaitToReadAsync(cancel);
                        TimeSpan totalWaitTime = TimeSpan.Zero;
                        while (StrategyExecutionChannel.Reader.Count < expectedUpdates
                               && totalWaitTime < TimeSpan.FromSeconds(5))
                        {
                            await Task.Delay(100, cancel);
                            totalWaitTime += TimeSpan.FromMilliseconds(100);
                        }

                        var hasInconsistent = Strategies.Values.Any(x => !x.ConsistentData);
                        if (hasInconsistent)
                        {
                            m_logger.LogWarning("Some strategies have inconsistent data. Reinitialize.");
                            await ReInitializeStrategies(cancel);
                        }

                        var strategyState = await UpdateTradingStatesAsync(cancel);
                        m_logger.LogInformation(
                            "Total long exposure: {LongExposure}, total short exposure: {ShortExposure}, long WE: {LongWE}, short WE: {ShortWE}",
                            strategyState.TotalLongExposure,
                            strategyState.TotalShortExposure,
                            strategyState.TotalWalletLongExposure,
                            strategyState.TotalWalletShortExposure);


                        List<string> symbolsToProcess = new List<string>();
                        using (await Lock.LockAsync())
                        {
                            while (StrategyExecutionChannel.Reader.TryRead(out var symbol))
                                symbolsToProcess.Add(symbol);

                            var inTradeSymbols = symbolsToProcess
                                .Where(x => Strategies.TryGetValue(x, out var strategy) && strategy.IsInTrade)
                                .Distinct()
                                .ToArray();
                            // by default already trading strategies can only maintain existing positions
                            Dictionary<string, ExecuteParams> executeParams =
                                inTradeSymbols.ToDictionary(x => x, _ => new ExecuteParams(false, false));

                            var inLongTradeSymbols = Strategies.Values.Where(x => x.IsInLongTrade).ToArray();
                            var inShortTradeSymbols = Strategies.Values.Where(x => x.IsInShortTrade).ToArray();
                            m_logger.LogInformation(
                                "Long strategies: '{LongStrategies}', short strategies: '{ShortStrategies}'",
                                inLongTradeSymbols.Length, inShortTradeSymbols.Length);

                            int remainingLongSlots = dynamicBotCount.MaxLongStrategies - inLongTradeSymbols.Length;
                            LogRemainingLongSlots(remainingLongSlots);
                            int remainingShortSlots = dynamicBotCount.MaxShortStrategies - inShortTradeSymbols.Length;
                            LogRemainingShortSlots(remainingShortSlots);
                            bool canAddLongPositions = remainingLongSlots > 0
                                                       && strategyState.TotalWalletLongExposure.HasValue
                                                       && strategyState.TotalWalletLongExposure.Value <
                                                       dynamicBotCount.TargetLongExposure;
                            bool canAddShortPositions = remainingShortSlots > 0
                                                        && strategyState.TotalWalletShortExposure.HasValue
                                                        && strategyState.TotalWalletShortExposure.Value <
                                                        dynamicBotCount.TargetShortExposure;
                            m_logger.LogInformation(
                                "Can add long positions: '{CanAddLongPositions}', can add short positions: '{CanAddShortPositions}'. Long throttler: '{LongThrottler}' Short throttler: '{LongThrottler}'",
                                canAddLongPositions,
                                canAddShortPositions,
                                strategyLongThrottler.AvailableTokens,
                                strategyShortThrottler.AvailableTokens);
                            // we need to put it back to hashset, we might open opposite position on the same symbol
                            HashSet<string> tradeSymbols = new HashSet<string>();
                            foreach (string inTradeSymbol in inTradeSymbols)
                                tradeSymbols.Add(inTradeSymbol);

                            if (canAddLongPositions)
                            {
                                int longStrategiesPerStep = Math.Min(dynamicBotCount.MaxDynamicStrategyOpenPerStep,
                                    remainingLongSlots);
                                var longStrategyCandidates = Strategies.Select(x => new
                                    {
                                        Strategy = x,
                                        x.Value.Indicators
                                    })
                                    .Where(x => x.Indicators.Any(i =>
                                        i.Name == nameof(IndicatorType.MainTimeFrameVolume) && i.Value is decimal))
                                    .Where(x => !x.Strategy.Value.IsInLongTrade && x.Strategy.Value.HasBuySignal &&
                                                x.Strategy.Value.DynamicQtyLong.HasValue)
                                    .Select(x => new
                                    {
                                        Strategy = x,
                                        MainTimeFrameVolume = (decimal)x.Indicators
                                            .First(i => i.Name == nameof(IndicatorType.MainTimeFrameVolume)).Value
                                    })
                                    .OrderByDescending(x => x.MainTimeFrameVolume)
                                    .Take(longStrategiesPerStep)
                                    .Select(x => x.Strategy.Strategy.Value.Symbol)
                                    .ToArray();
                                foreach (string longStrategyCandidate in longStrategyCandidates)
                                {
                                    if (strategyLongThrottler.ShouldThrottle(1, out _))
                                        break;
                                    m_logger.LogInformation("Adding long strategy '{LongStrategyCandidate}'",
                                        longStrategyCandidate);
                                    tradeSymbols.Add(longStrategyCandidate);
                                    executeParams.TryGetValue(longStrategyCandidate, out var existingParams);
                                    executeParams[longStrategyCandidate] = existingParams with { AllowLongOpen = true };
                                }
                            }

                            if (canAddShortPositions)
                            {
                                int shortStrategiesPerStep = Math.Min(dynamicBotCount.MaxDynamicStrategyOpenPerStep,
                                    remainingShortSlots);
                                var shortStrategyCandidates = Strategies.Select(x => new
                                    {
                                        Strategy = x,
                                        x.Value.Indicators
                                    })
                                    .Where(x => x.Indicators.Any(i =>
                                        i.Name == nameof(IndicatorType.MainTimeFrameVolume) && i.Value is decimal))
                                    .Where(x => !x.Strategy.Value.IsInShortTrade && x.Strategy.Value.HasSellSignal &&
                                                x.Strategy.Value.DynamicQtyShort.HasValue)
                                    .Select(x => new
                                    {
                                        Strategy = x,
                                        MainTimeFrameVolume = (decimal)x.Indicators
                                            .First(i => i.Name == nameof(IndicatorType.MainTimeFrameVolume)).Value
                                    })
                                    .OrderByDescending(x => x.MainTimeFrameVolume)
                                    .Take(shortStrategiesPerStep)
                                    .Select(x => x.Strategy.Strategy.Value.Symbol)
                                    .ToArray();
                                foreach (string shortStrategyCandidate in shortStrategyCandidates)
                                {
                                    if (strategyShortThrottler.ShouldThrottle(1, out _))
                                        break;
                                    m_logger.LogInformation("Adding short strategy '{ShortStrategyCandidate}'",
                                        shortStrategyCandidate);
                                    tradeSymbols.Add(shortStrategyCandidate);
                                    executeParams.TryGetValue(shortStrategyCandidate, out var existingParams);
                                    executeParams[shortStrategyCandidate] =
                                        existingParams with { AllowShortOpen = true };
                                }
                            }

                            List<Task> executionTasks = new List<Task>();
                            await PrepareStrategyExecutionAsync(executionTasks, tradeSymbols.ToArray(), executeParams,
                                cancel);
                            await Task.WhenAll(executionTasks);
                            DateTime utcNow = DateTime.UtcNow;
                            Interlocked.Exchange(ref m_lastExecutionTimestamp, utcNow.Ticks);
                        }
                    }
                    catch (Exception e)
                    {
                        m_logger.LogError(e, "Error while executing strategies");
                    }
                    finally
                    {
                        // wait a little bit so we are not rate limited
                        await Task.Delay(TimeSpan.FromSeconds(10), cancel);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                m_logger.LogInformation("Strategy execution cancelled.");
            }
        }
    }
}
