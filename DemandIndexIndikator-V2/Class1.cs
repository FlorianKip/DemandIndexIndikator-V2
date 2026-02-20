using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
using System.Resources;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Net.NetworkInformation;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System;

using ATAS.Indicators;
using ATAS.Strategies;
using ATAS.Indicators.Technical;
using ATAS.Indicators.Other;
using ATAS.Indicators.Technical.Properties;
using ATAS.Strategies.Chart;
using ATAS.DataFeedsCore;

using Utils.Common.Logging;

using OFT.Attributes;
using OFT.Localization;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;
using System.Runtime.Intrinsics.X86;
using System.Drawing;

namespace ATAS.Indicators.Technical
{



    [DisplayName("DemandIndexIndikator-V2")]
    public class DemandIndexIndikator : Indicator
    {
        #region Private fields
        private int _currentBar; // Aktuelle Bar abspeichern
        private int _prevBar; // Vorherige Bar abspeichern
        private bool once_a_bar = false;
        private bool inPosition;

        private readonly Demand _di = new Demand(); // Beispielvariable für Demand-Indikator
        private readonly SuperTrend _supertrend = new SuperTrend(); // Beispielvariable für Supertrend
        private readonly HeikenAshiSmoothed _ha = new HeikenAshiSmoothed(); // Beispielvariable für Supertrend
        private readonly EMA _ema = new EMA();
        private readonly VWAP _vwap = new VWAP();
        private readonly ValueArea _va = new ValueArea();
        private readonly DynamicLevels _dynamicLevels = new DynamicLevels();

        private ValueDataSeries _arrowShort = new ValueDataSeries("Short")
        {
            VisualType = VisualMode.DownArrow,
            ShowZeroValue = false,
            //Color = Color.Red.Convert(),
            Width = 1
        };

        private ValueDataSeries _arrowLong = new ValueDataSeries("Long")
        {
            VisualType = VisualMode.UpArrow,
            ShowZeroValue = false,
            //Color = Color.Green.Convert(),
            Width = 1
        };

        #endregion

        //Public Einstellungen, die man in ATAS in den Strategy Einstellungen anpassen kann
        // Es ist sinnvoll alle Variablen editierbar zu machen, die man anpassen muss. (Zum Beispiel SL oder TP) (Kann man sich ausdenken)
        #region


        #endregion

        public DemandIndexIndikator()
        {
            // Initialisierung von Indikatorparametern
            _supertrend.Period = 14;
            _supertrend.Multiplier = 2.0m;
            _ema.Period = 100;
            _dynamicLevels.Type = DynamicLevels.MiddleClusterType.Volume;
            _dynamicLevels.PeriodFrame = DynamicLevels.Period.Daily;
            _dynamicLevels.Filter = 0m;
            _dynamicLevels.Days = 20;
            _dynamicLevels.VizualizationType = DynamicLevels.VolumeVizualizationType.Accumulated;

        }

        protected override void OnCalculate(int bar, decimal value)
        {
            // Sicherstellen, dass alle Indikatoren initialisiert sind
            if (_supertrend == null || _ema == null || _vwap == null || _di == null || _va == null)
            {
                this.LogInfo("Ein oder mehrere Indikator-Instanzen sind nicht initialisiert.");
                return;
            }

            // Sicherstellen, dass TradingManager und Position existieren
            //if (TradingManager?.Position == null)
            //{
            //    this.LogInfo("TradingManager oder Position ist null.");
            //    return;
            //}

            _prevBar = _currentBar;
            _currentBar = bar;

            // Logik für die Verarbeitung pro Bar
            if (_prevBar != _currentBar)
            {
                once_a_bar = false;
            }

            inPosition = TradingManager.Position?.IsInPosition ?? false;

            if (!inPosition && !once_a_bar)
            {
                var candle = GetCandle(_prevBar);
                if (candle == null)
                {
                    this.LogInfo($"Candle für Bar {_prevBar} nicht verfügbar.");
                    return;
                }

                // Statusbestimmungen der Indikatoren
                string supertrendStatus = _supertrend[_prevBar] != 0 ? "Long" :
                    (_supertrend[_prevBar] == 0 ? "Short" : "");

                string emaStatus = _ema[_prevBar] > candle.Close ? "Short" :
                    (_ema[_prevBar] < candle.Close ? "Long" : "");

                string vwapStatus = _vwap[_prevBar] > GetCandle(_prevBar).Close ? "Short" :
                   (_vwap[_prevBar] < GetCandle(_prevBar).Close ? "Long" : "");

                var haCandles = (CandleDataSeries)_ha.DataSeries[1];
                Candle _lastHaCandle = haCandles[_prevBar];
                var midPriceLastHaCandle = (Math.Max(_lastHaCandle.High, _lastHaCandle.Low) + Math.Min(_lastHaCandle.High, _lastHaCandle.Low)) / 2;

                string haStatus = midPriceLastHaCandle > GetCandle(_prevBar).Close ? "Short" :
                    (midPriceLastHaCandle < GetCandle(_prevBar).Close ? "Long" : "");

                decimal _valueArea1stLine = (decimal)_dynamicLevels.DataSeries[2][_prevBar]; // Value Area 1st line VAH

                decimal _valueArea2ndLine = (decimal)_dynamicLevels.DataSeries[3][_prevBar]; // Value Area 2nd line VAL

                string vaStatus = _valueArea1stLine < GetCandle(_prevBar).Close ? "> VAH (Long)" :
                   (_valueArea2ndLine > GetCandle(_prevBar).Close ? "< VAL (Short)" : "in VA (Range)");

                decimal oscillatedVWAP = ((GetCandle(_prevBar).Close - _vwap[_prevBar]) / _vwap[_prevBar]) * 100;

                decimal _DynPOC = (decimal)_dynamicLevels.DataSeries[0][_prevBar]; // POC Value

                decimal oscillatedPOC = ((GetCandle(_prevBar).Close - _DynPOC) / _DynPOC) * 100;

                decimal _VAMid = ((_valueArea1stLine - _valueArea2ndLine) / 2) + _valueArea2ndLine;

                decimal oscillatedVAMid = ((GetCandle(_prevBar).Close - _VAMid) / _VAMid) * 100;



                // Überprüfen, ob der aktuelle und der vorherige Bar gültige Werte liefern
                if (_di == null)
                {
                    this.LogInfo("Ungültiger Zugriff auf Demand-Index-Objekt");
                    return;
                }

                bool diCrossLong = _di[_prevBar - 1] < 0 && _di[_prevBar] > 0;
                bool diCrossShort = _di[_prevBar - 1] > 0 && _di[_prevBar] < 0;

                // Alerts und Logging bei bestimmten Marktbedingungen
                if (diCrossLong && vaStatus.Equals("> VAH (Long)")) //  && emaStatus.Equals("Long") && haStatus.Equals("Long") && vwapStatus.Equals("Long") 
                {
                    AddArrowLong(_prevBar, GetCandle(_prevBar).Close);
                    this.LogInfo("");
                    this.LogInfo($"oscillatedVWAP: {oscillatedVWAP}");
                    this.LogInfo($"oscillatedPOC: {oscillatedPOC}");
                    this.LogInfo($"oscillatedVAMid: {oscillatedVAMid}");
                    this.LogInfo($"POC: {(decimal)_dynamicLevels.DataSeries[0][_prevBar]}");
                    this.LogInfo($"VAL: {_valueArea2ndLine}");
                    this.LogInfo($"VAH: {_valueArea1stLine}");
                    this.LogInfo($"currentPrice (Close): {GetCandle(_prevBar).Close}");
                    this.LogInfo($"VA: {vaStatus}");
                    this.LogInfo($"Heikin Ashi: {haStatus}");
                    this.LogInfo($"Supertrend: {supertrendStatus}");
                    this.LogInfo($"EMA: {emaStatus}");
                    this.LogInfo($"VWAP: {vwapStatus}");
                    this.LogInfo("DI Cross Long!");
                    //AddAlert("alert1", $"DI Cross Long! VWAP: {vwapStatus} EMA: {emaStatus} Supertrend: {supertrendStatus} VA: {vaStatus}");
                    AddAlert("alert1", $"DI Cross Long!");
                }
                else if (diCrossShort && vaStatus.Equals("< VAL (Short)")) //  && emaStatus.Equals("Short") && haStatus.Equals("Short")  && vwapStatus.Equals("Short")
                {
                    AddArrowShort(_prevBar, GetCandle(_prevBar).Close);
                    this.LogInfo("");
                    this.LogInfo($"oscillatedVWAP: {oscillatedVWAP}");
                    this.LogInfo($"oscillatedPOC: {oscillatedPOC}");
                    this.LogInfo($"oscillatedVAMid: {oscillatedVAMid}");
                    this.LogInfo($"POC: {(decimal)_dynamicLevels.DataSeries[0][_prevBar]}");
                    this.LogInfo($"VAL: {_valueArea2ndLine}");
                    this.LogInfo($"VAH: {_valueArea1stLine}");
                    this.LogInfo($"currentPrice (Close): {GetCandle(_prevBar).Close}");
                    this.LogInfo($"VA: {vaStatus}");
                    this.LogInfo($"Heikin Ashi: {haStatus}");
                    this.LogInfo($"Supertrend: {supertrendStatus}");
                    this.LogInfo($"EMA: {emaStatus}");
                    this.LogInfo($"VWAP: {vwapStatus}");
                    this.LogInfo("DI Cross Short!");
                    //AddAlert("alert1", $"DI Cross Short! VWAP: {vwapStatus} EMA: {emaStatus} Supertrend: {supertrendStatus} VA: {vaStatus}");
                    AddAlert("alert1", $"DI Cross Short!");
                }

                once_a_bar = true;
            }
        }

        public void AddArrowLong(int bar, decimal price)
        {
            _arrowLong[bar] = price;
        }

        public void AddArrowShort(int bar, decimal price)
        {
            _arrowShort[bar] = price;
        }


    }
}