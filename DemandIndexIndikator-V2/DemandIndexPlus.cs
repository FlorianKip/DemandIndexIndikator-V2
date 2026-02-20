namespace DemandIndexPlus
{
    using System;
    using System.ComponentModel;
    using System.ComponentModel.DataAnnotations;
    using System.Windows.Media;
    using ATAS.Indicators;
    using ATAS.Indicators.Drawing;
    using ATAS.Indicators.Technical;
    using OFT.Attributes;
    using Utils.Common.Logging;

    /// <summary>
    /// DemandIndexPlus - Extended Demand Index Indicator with candle coloring, 
    /// correlation filters, extreme zones visualization, and alerts
    /// </summary>
    [DisplayName("DemandIndexPlus")]
    [Category("Custom")]
    [HelpLink("https://help.atas.net/support/solutions/articles/72000602288")]
    public class DemandIndexPlus : Indicator
    {
        #region Constants

        private const int MinimumBarsRequired = 3;

        #endregion

        #region Private Fields - Indicators

        // Core Demand Index calculation components (embedded from original)
        private readonly EMA _emaBp = new() { Period = 10 };
        private readonly EMA _emaRange = new() { Period = 10 };
        private readonly EMA _emaSp = new() { Period = 10 };
        private readonly EMA _emaVolume = new() { Period = 10 };
        private readonly SMA _sma = new() { Period = 10 };
        private readonly ValueDataSeries _priceSumSeries = new("PriceSum");

        // Additional indicators for correlation filters
        private readonly VWAP _vwap = new();
        private readonly DynamicLevels _dynamicLevels = new();
        private readonly DailyLines _dailyLines = new();

        #endregion

        #region Private Fields - Data Series

        /// <summary>Main DI line</summary>
        private readonly ValueDataSeries _diSeries = new("DI", "Demand Index")
        {
            Color = Colors.DodgerBlue,
            Width = 2,
            ShowZeroValue = true
        };

        /// <summary>SMA of DI</summary>
        private readonly ValueDataSeries _smaSeries = new("SMA", "DI Average")
        {
            Color = Colors.Yellow,
            Width = 1
        };

        /// <summary>Candle coloring on price chart</summary>
        private readonly PaintbarsDataSeries _paintBars = new("SignalCandles", "Signal Candles")
        {
            IsHidden = true
        };

        #endregion

        #region Private Fields - State Tracking

        /// <summary>Track if we were in overbought zone (for extreme reversal detection)</summary>
        private bool _wasInOverbought = false;

        /// <summary>Track if we were in oversold zone (for extreme reversal detection)</summary>
        private bool _wasInOversold = false;

        /// <summary>Track if first reversal candle was already painted (to paint only first)</summary>
        private bool _extremeReversalPaintedOverbought = false;

        /// <summary>Track if first reversal candle was already painted (to paint only first)</summary>
        private bool _extremeReversalPaintedOversold = false;

        /// <summary>Last bar processed (for state reset)</summary>
        private int _lastBar = -1;

        #endregion

        #region Private Fields - Backing Fields for Properties

        private int _extremeLevel = 60;
        private bool _useVAFilter = false;
        private bool _useVWAPFilter = false;
        private bool _usePrevDayFilter = false;
        private bool _showExtremeLines = true;
        private bool _colorDIExtreme = true;
        private bool _useTimeFilter = false;
        private TimeSpan _tradingStartTime = new TimeSpan(9, 30, 0);
        private TimeSpan _tradingEndTime = new TimeSpan(16, 0, 0);
        private bool _enableAlerts = true;
        private Color _crossLongColor = Colors.Lime;
        private Color _crossShortColor = Colors.Red;
        private Color _extremeReversalColor = Colors.Orange;

        #endregion

        #region Public Properties - General Settings

        [Display(Name = "Extreme Level", Order = 10, GroupName = "General", Description = "Level for overbought/oversold detection")]
        [ATAS.Indicators.Parameter]
        [Range(1, 200)]
        public int ExtremeLevel 
        { 
            get => _extremeLevel;
            set
            {
                _extremeLevel = value;
                RecalculateValues();
            }
        }

        [Display(Name = "BuySell Power Period", Order = 11, GroupName = "General")]
        [ATAS.Indicators.Parameter]
        [Range(1, 10000)]
        public int BuySellPower
        {
            get => _emaRange.Period;
            set
            {
                _emaRange.Period = _emaVolume.Period = value;
                RecalculateValues();
            }
        }

        [Display(Name = "BuySell Smooth Period", Order = 12, GroupName = "General")]
        [ATAS.Indicators.Parameter]
        [Range(1, 10000)]
        public int BuySellSmooth
        {
            get => _emaBp.Period;
            set
            {
                _emaBp.Period = _emaSp.Period = value;
                RecalculateValues();
            }
        }

        [Display(Name = "SMA Period", Order = 13, GroupName = "General")]
        [ATAS.Indicators.Parameter]
        [Range(1, 10000)]
        public int SmaPeriod
        {
            get => _sma.Period;
            set
            {
                _sma.Period = value;
                RecalculateValues();
            }
        }

        #endregion

        #region Public Properties - Candle Coloring

        [Display(Name = "Cross Long Color", Order = 20, GroupName = "Candle Coloring")]
        public Color CrossLongColor 
        { 
            get => _crossLongColor;
            set
            {
                _crossLongColor = value;
                RecalculateValues();
            }
        }

        [Display(Name = "Cross Short Color", Order = 21, GroupName = "Candle Coloring")]
        public Color CrossShortColor 
        { 
            get => _crossShortColor;
            set
            {
                _crossShortColor = value;
                RecalculateValues();
            }
        }

        [Display(Name = "Extreme Reversal Color", Order = 22, GroupName = "Candle Coloring")]
        public Color ExtremeReversalColor 
        { 
            get => _extremeReversalColor;
            set
            {
                _extremeReversalColor = value;
                RecalculateValues();
            }
        }

        #endregion

        #region Public Properties - Correlation Filters

        [Display(Name = "Use VA Filter", Order = 30, GroupName = "Correlation Filters", Description = "Long only > VAH, Short only < VAL")]
        [ATAS.Indicators.Parameter]
        public bool UseVAFilter 
        { 
            get => _useVAFilter;
            set
            {
                _useVAFilter = value;
                RecalculateValues();
            }
        }

        [Display(Name = "Use VWAP Filter", Order = 31, GroupName = "Correlation Filters", Description = "Long only > VWAP, Short only < VWAP")]
        [ATAS.Indicators.Parameter]
        public bool UseVWAPFilter 
        { 
            get => _useVWAPFilter;
            set
            {
                _useVWAPFilter = value;
                RecalculateValues();
            }
        }

        [Display(Name = "Use PrevDay Filter", Order = 32, GroupName = "Correlation Filters", Description = "Long only > PrevDay High, Short only < PrevDay Low")]
        [ATAS.Indicators.Parameter]
        public bool UsePrevDayFilter 
        { 
            get => _usePrevDayFilter;
            set
            {
                _usePrevDayFilter = value;
                RecalculateValues();
            }
        }

        #endregion

        #region Public Properties - Visual Settings

        [Display(Name = "Show Extreme Lines", Order = 40, GroupName = "Visuals", Description = "Show dashed horizontal lines at extreme levels")]
        [ATAS.Indicators.Parameter]
        public bool ShowExtremeLines 
        { 
            get => _showExtremeLines;
            set
            {
                _showExtremeLines = value;
                UpdateExtremeLines();
                RecalculateValues();
            }
        }

        [Display(Name = "Color DI in Extreme", Order = 41, GroupName = "Visuals", Description = "Color DI line red when overbought, green when oversold")]
        [ATAS.Indicators.Parameter]
        public bool ColorDIExtreme 
        { 
            get => _colorDIExtreme;
            set
            {
                _colorDIExtreme = value;
                RecalculateValues();
            }
        }

        #endregion

        #region Public Properties - Time Filter

        [Display(Name = "Use Time Filter", Order = 50, GroupName = "Time Filter")]
        [ATAS.Indicators.Parameter]
        public bool UseTimeFilter 
        { 
            get => _useTimeFilter;
            set
            {
                _useTimeFilter = value;
                RecalculateValues();
            }
        }

        [Display(Name = "Trading Start Time", Order = 51, GroupName = "Time Filter")]
        [ATAS.Indicators.Parameter]
        public TimeSpan TradingStartTime 
        { 
            get => _tradingStartTime;
            set
            {
                _tradingStartTime = value;
                RecalculateValues();
            }
        }

        [Display(Name = "Trading End Time", Order = 52, GroupName = "Time Filter")]
        [ATAS.Indicators.Parameter]
        public TimeSpan TradingEndTime 
        { 
            get => _tradingEndTime;
            set
            {
                _tradingEndTime = value;
                RecalculateValues();
            }
        }

        #endregion

        #region Public Properties - Alerts

        [Display(Name = "Enable Alerts", Order = 60, GroupName = "Alerts")]
        [ATAS.Indicators.Parameter]
        public bool EnableAlerts 
        { 
            get => _enableAlerts;
            set
            {
                _enableAlerts = value;
                // No recalculation needed for alerts toggle
            }
        }

        #endregion

        #region Constructor

        public DemandIndexPlus() : base(true)
        {
            // DI panel setup
            Panel = IndicatorDataProvider.NewPanel;

            // Initialize Dynamic Levels for VA
            _dynamicLevels.Type = DynamicLevels.MiddleClusterType.Volume;
            _dynamicLevels.PeriodFrame = DynamicLevels.Period.Daily;
            _dynamicLevels.Filter = 0m;
            _dynamicLevels.Days = 20;
            _dynamicLevels.VizualizationType = DynamicLevels.VolumeVizualizationType.Accumulated;

            // Initialize Daily Lines for PrevDay
            _dailyLines.Period = DailyLines.PeriodType.PreviousDay;

            // Add child indicators
            Add(_dynamicLevels);
            Add(_dailyLines);
            Add(_vwap);

            // Register data series
            DataSeries[0] = _diSeries;
            DataSeries.Add(_smaSeries);
            DataSeries.Add(_paintBars);
        }

        #endregion

        #region Lifecycle

        protected override void OnInitialize()
        {
            base.OnInitialize();

            // Add extreme level lines if enabled
            UpdateExtremeLines();
        }

        protected override void OnRecalculate()
        {
            base.OnRecalculate();
            
            // Reset state tracking
            _wasInOverbought = false;
            _wasInOversold = false;
            _extremeReversalPaintedOverbought = false;
            _extremeReversalPaintedOversold = false;
            _lastBar = -1;

            UpdateExtremeLines();
        }

        /// <summary>
        /// Update extreme level horizontal lines
        /// </summary>
        private void UpdateExtremeLines()
        {
            // Clear existing lines
            LineSeries.Clear();

            // Zero line (always visible)
            var zeroLine = new LineSeries("Zero", "Zero Line")
            {
                Color = System.Drawing.Color.Gray.Convert(),
                Value = 0,
                Width = 1
            };
            LineSeries.Add(zeroLine);

            if (_showExtremeLines)
            {
                // Overbought line (red - sell zone)
                var overboughtLine = new LineSeries("Overbought", $"+{_extremeLevel} (Overbought)")
                {
                    Color = System.Drawing.Color.Red.Convert(),
                    Value = _extremeLevel,
                    Width = 1
                };
                LineSeries.Add(overboughtLine);

                // Oversold line (green - buy zone)
                var oversoldLine = new LineSeries("Oversold", $"-{_extremeLevel} (Oversold)")
                {
                    Color = System.Drawing.Color.Green.Convert(),
                    Value = -_extremeLevel,
                    Width = 1
                };
                LineSeries.Add(oversoldLine);
            }
        }

        #endregion

        #region Main Calculation

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar < MinimumBarsRequired) return;

            // Calculate DI value
            decimal diValue = CalculateDemandIndex(bar);
            decimal smaValue = _sma.Calculate(bar, diValue);

            _diSeries[bar] = diValue;
            _smaSeries[bar] = smaValue;

            // Color DI line in extreme zones
            if (_colorDIExtreme)
            {
                ApplyDIColoring(bar, diValue);
            }

            // Get candle for pattern detection
            var candle = GetCandle(bar);
            var prevCandle = GetCandle(bar - 1);

            // Check time filter
            if (!IsWithinTradingTime(candle.Time))
            {
                _paintBars[bar] = null;
                return;
            }

            // Reset state on new bar
            if (bar != _lastBar)
            {
                _lastBar = bar;
            }

            // Detect signals and apply candle coloring
            DetectAndPaintSignals(bar, diValue, candle, prevCandle);
        }

        #endregion

        #region DI Calculation (from original Demand.cs)

        /// <summary>
        /// Calculate Demand Index value (embedded from original ATAS Demand indicator)
        /// </summary>
        private decimal CalculateDemandIndex(int bar)
        {
            var candle = GetCandle(bar);
            _priceSumSeries[bar] = candle.High + candle.Low + 2 * candle.Close;
            _emaVolume.Calculate(bar, candle.Volume);

            if (bar == 0)
            {
                return 0;
            }

            var firstCandle = GetCandle(0);
            var bp = 0m;

            if (_emaVolume[bar] != 0 && firstCandle.High != firstCandle.Low && _priceSumSeries[bar] != 0)
            {
                if (_priceSumSeries[bar] < _priceSumSeries[bar - 1])
                {
                    var expValue = Math.Exp(0.375 * (double)(
                        (_priceSumSeries[bar] + _priceSumSeries[bar - 1]) / (firstCandle.High - firstCandle.Low) *
                        (_priceSumSeries[bar - 1] - _priceSumSeries[bar]) / _priceSumSeries[bar]
                    ));
                    bp = candle.Volume / _emaVolume[bar] / ToDecimal(expValue);
                }
                else
                {
                    bp = candle.Volume / _emaVolume[bar];
                }
            }
            else if (_emaVolume[bar - 1] != 0)
            {
                bp = candle.Volume / _emaVolume[bar - 1];
            }

            var sp = 0m;

            if (_emaVolume[bar] != 0 && firstCandle.High != firstCandle.Low && _priceSumSeries[bar - 1] != 0)
            {
                if (_priceSumSeries[bar] <= _priceSumSeries[bar - 1])
                {
                    sp = candle.Volume / _emaVolume[bar];
                }
                else
                {
                    var spValue = (double)(candle.Volume / _emaVolume[bar]) /
                        Math.Exp(0.375 * (double)(
                            (_priceSumSeries[bar] + _priceSumSeries[bar - 1]) / (firstCandle.High - firstCandle.Low) *
                            (_priceSumSeries[bar] - _priceSumSeries[bar - 1]) / _priceSumSeries[bar - 1]
                        ));
                    sp = ToDecimal(spValue);
                }
            }
            else if (_emaVolume[bar - 1] != 0)
            {
                sp = candle.Volume / _emaVolume[bar - 1];
            }

            _emaBp.Calculate(bar, bp);
            _emaSp.Calculate(bar, sp);

            var q = 0m;

            if (_emaBp[bar] > _emaSp[bar])
                q = _emaBp[bar] == 0 ? 0 : _emaSp[bar] / _emaBp[bar];
            else if (_emaBp[bar] < _emaSp[bar])
                q = _emaSp[bar] == 0 ? 0 : _emaBp[bar] / _emaSp[bar];
            else
                q = 1;

            var di = 0m;

            if (_emaSp[bar] <= _emaBp[bar])
                di = 100 * (1 - q);
            else
                di = 100 * (q - 1);

            return di;
        }

        private static decimal ToDecimal(double value)
        {
            return value switch
            {
                >= (double)decimal.MaxValue => decimal.MaxValue,
                <= (double)decimal.MinValue => decimal.MinValue,
                _ => (decimal)value
            };
        }

        #endregion

        #region Signal Detection

        /// <summary>
        /// Detect signals and paint candles accordingly
        /// </summary>
        private void DetectAndPaintSignals(int bar, decimal diValue, IndicatorCandle candle, IndicatorCandle prevCandle)
        {
            if (bar < 3) return;

            decimal diPrev1 = _diSeries[bar - 1];
            decimal diPrev2 = _diSeries[bar - 2];

            bool isLongCandle = candle.Close > candle.Open;
            bool isShortCandle = candle.Close < candle.Open;

            // Get correlation filter values
            decimal vah = (decimal)_dynamicLevels.DataSeries[2][bar];
            decimal val = (decimal)_dynamicLevels.DataSeries[3][bar];
            decimal vwapValue = _vwap[bar];
            decimal prevDayHigh = (decimal)_dailyLines.DataSeries[1][bar]; // High
            decimal prevDayLow = (decimal)_dailyLines.DataSeries[2][bar];  // Low

            // Track extreme zone state
            bool currentlyOverbought = diPrev1 > _extremeLevel;
            bool currentlyOversold = diPrev1 < -_extremeLevel;

            // Check for DI Cross signals
            bool diCrossLong = diPrev1 > 0 && diPrev2 < 0;
            bool diCrossShort = diPrev1 < 0 && diPrev2 > 0;

            // Check for Extreme Reversal signals
            bool extremeReversalLong = _wasInOversold && isLongCandle && !_extremeReversalPaintedOversold;
            bool extremeReversalShort = _wasInOverbought && isShortCandle && !_extremeReversalPaintedOverbought;

            // Apply correlation filters
            bool longFilterPassed = CheckLongFilters(candle.Close, vah, vwapValue, prevDayHigh);
            bool shortFilterPassed = CheckShortFilters(candle.Close, val, vwapValue, prevDayLow);

            // Determine which signal to show (priority: Extreme Reversal > Cross)
            Color? paintColor = null;
            string alertMessage = null;

            if (extremeReversalLong && longFilterPassed)
            {
                paintColor = _extremeReversalColor;
                alertMessage = "DI Extreme Reversal Long (Oversold)";
                _extremeReversalPaintedOversold = true;
            }
            else if (extremeReversalShort && shortFilterPassed)
            {
                paintColor = _extremeReversalColor;
                alertMessage = "DI Extreme Reversal Short (Overbought)";
                _extremeReversalPaintedOverbought = true;
            }
            else if (diCrossLong && longFilterPassed)
            {
                paintColor = _crossLongColor;
                alertMessage = "DI Cross Long Signal";
            }
            else if (diCrossShort && shortFilterPassed)
            {
                paintColor = _crossShortColor;
                alertMessage = "DI Cross Short Signal";
            }

            // Apply candle coloring
            _paintBars[bar] = paintColor;

            // Fire alert
            if (alertMessage != null && _enableAlerts)
            {
                AddAlert("DemandIndexPlus", alertMessage);
            }

            // Update extreme zone tracking
            if (currentlyOverbought)
            {
                _wasInOverbought = true;
                _extremeReversalPaintedOverbought = false;
            }
            else if (!currentlyOverbought && diPrev1 < _extremeLevel * 0.5m)
            {
                // Reset when DI drops significantly below extreme
                _wasInOverbought = false;
            }

            if (currentlyOversold)
            {
                _wasInOversold = true;
                _extremeReversalPaintedOversold = false;
            }
            else if (!currentlyOversold && diPrev1 > -_extremeLevel * 0.5m)
            {
                // Reset when DI rises significantly above extreme
                _wasInOversold = false;
            }
        }

        #endregion

        #region Filter Checks

        /// <summary>
        /// Check if long signal passes all enabled filters
        /// </summary>
        private bool CheckLongFilters(decimal price, decimal vah, decimal vwap, decimal prevDayHigh)
        {
            if (_useVAFilter && price <= vah)
                return false;

            if (_useVWAPFilter && price <= vwap)
                return false;

            if (_usePrevDayFilter && price <= prevDayHigh)
                return false;

            return true;
        }

        /// <summary>
        /// Check if short signal passes all enabled filters
        /// </summary>
        private bool CheckShortFilters(decimal price, decimal val, decimal vwap, decimal prevDayLow)
        {
            if (_useVAFilter && price >= val)
                return false;

            if (_useVWAPFilter && price >= vwap)
                return false;

            if (_usePrevDayFilter && price >= prevDayLow)
                return false;

            return true;
        }

        /// <summary>
        /// Check if current time is within trading hours
        /// </summary>
        private bool IsWithinTradingTime(DateTime candleTime)
        {
            if (!_useTimeFilter)
                return true;

            TimeSpan currentTime = candleTime.TimeOfDay;
            return currentTime >= _tradingStartTime && currentTime <= _tradingEndTime;
        }

        #endregion

        #region DI Line Coloring

        /// <summary>
        /// Apply coloring to DI line based on extreme zones
        /// </summary>
        private void ApplyDIColoring(int bar, decimal diValue)
        {
            if (diValue > _extremeLevel)
            {
                // Overbought - Red
                _diSeries.Colors[bar] = Colors.Red.Convert();
            }
            else if (diValue < -_extremeLevel)
            {
                // Oversold - Green
                _diSeries.Colors[bar] = Colors.Green.Convert();
            }
            else
            {
                // Normal - Default blue
                _diSeries.Colors[bar] = Colors.DodgerBlue.Convert();
            }
        }

        #endregion
    }
}
