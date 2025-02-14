using Game;
using Game.Systems;
using Game.Constants;
using Game.Systems.Trade;
using UnityEngine;
using KL.Utils;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Game.Platform;
using Game.Data;
using Game.Utils;
using System.Runtime.CompilerServices;
using System;
using Unity.Mathematics;
using Game.Components;
using System.Text;
using System.Text.RegularExpressions;
using Game.Systems.Crafting;

namespace MiscMod
{
    [HarmonyPatch]
    public sealed class MoreNumbersPatcher
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        private static void Register()
        {
            LogWarn("Being registered, applying the Harmony patches.");
            var harmony = new Harmony("com.MoreNumbers.patch");
            harmony.PatchAll();
        }
        public static void LogWarn(string msg)
        {
            D.Warn($"[MoreNumbers] {msg}");
        }
        public static void LogErr(string msg)
        {
            D.Err($"[MoreNumbers] {msg}");
        }

        static HashSet<(string, int, string, string)> errorsLogged = new();
        public static void LogErrOnce(string msg, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0, [CallerMemberName] string callerMember = "")
        {
            var key = (callerFile, callerLine, callerMember, msg);
            if (callerFile != "" && callerMember != "" && errorsLogged.Contains(key))
            {
                return;
            }
            errorsLogged.Add(key);
            LogErr(msg);
        }

        public static void AddToTooltip(UDB udb, Func<UDB, string> tooltip)
        {
            Func<UDB, string> oldTooltipFunction = udb.TooltipFunction;
            Func<UDB, string> newTooltipFunction = tooltip;
            if (oldTooltipFunction != null)
            {
                newTooltipFunction = x => $"{oldTooltipFunction(x)}\n{tooltip(x)}";
            }
            udb.TooltipFunction = newTooltipFunction;
        }

        [HarmonyPatch(typeof(ShipNavSys), nameof(ShipNavSys.GetUIDetails))]
        [HarmonyPostfix]
        private static void GetUIDetailsPatch(ShipNavSys __instance, List<UDB> res)
        {
            // The elements we want to modify is the "Planetary Flight" one. Let's locate it by name.
            var target = Texts.Beautify(T.ShipCapablePlanet);
            var el = ((IEnumerable<UDB>)res).Reverse().FirstOrDefault(x => x.Title == target);
            if (el == null)
            {
                LogErrOnce($"Failed to find the Planetary Flight element in the navigation UV; was searching for title {target}. This error will only be displayed once.");
                return;
            }
            AddToTooltip(el, x => ExtraPFData());
        }


        public static float MaxEnginePush()
        {
            return A.S.Sys.ShipNav.Engines
            .Where(engine => engine.IsPowered && engine.WorksIn("Sector"))
            .Select(engine => engine.PowerWithUpgrades)
            .Sum();
        }
        public static string ExtraPFData()
        {
            // duplicating calculations from ShipSys.OnTick
            var ShipMass = A.S.Sys.Areas.TotalAreaSize;
            float shipSpeedMin = Tunables.ShipSpeedMin;
            float shipSpeedMax = Tunables.ShipSpeedMax;

            // same but for max
            var maxEngineForce = MaxEnginePush();
            var trueMaxVelocity = Equations.EnginePush(maxEngineForce, ShipMass, shipSpeedMin, shipSpeedMax);
            float rawMaxAccel = maxEngineForce / ShipMass;
            float adjustedMaxAccel = math.sqrt(rawMaxAccel);
            var expectedMaxVelocity = math.lerp(shipSpeedMin, shipSpeedMax, Maths.Clamp(adjustedMaxAccel, 0f, 1f));

            List<string> parts = new()
            {
                $"Ship mass: {ShipMass} tiles",
                $"Max raw engine force: {maxEngineForce} kN",
                $"Max raw acceleration: {rawMaxAccel} kN/tile",
                $"Max speed factor (sqrt of acceleration): {adjustedMaxAccel}",
                $"Speed range: {shipSpeedMin} to {shipSpeedMax}",
                $"Final max speed: {trueMaxVelocity}"
            };
            if (math.abs(expectedMaxVelocity - trueMaxVelocity) > 1e-6)
            {
                LogErrOnce($"The velocity calculation doesn't match - probably the game changed and the mod needs to be updated. Expected to get {expectedMaxVelocity} but game returned {trueMaxVelocity}.");
                parts.Append($"Warning: velocity calculation doesn't match! The mod's math must be outdated.");
            }
            if (trueMaxVelocity == shipSpeedMax)
            {
                parts.Append($"You are currently at the ship velocity upper limit.");
            }
            return string.Join("\n", parts);

        }
    }
    [HarmonyPatch]
    class MaterialEnergyPatcher
    {
        [HarmonyPatch(typeof(Def), "AddMatInfo")]
        [HarmonyPostfix]
        private static void AddMatInfoPatch(Def __instance, StringBuilder sb)
        {
            // going to be a bit annoying to patch this, but oh well.
            // added parts are:
            // sb.Append(T.MatEnergyOutput);
            // sb.Append(": ");
            // sb.Append(Texts.WithColor(matType.EnergyOutput.ToString(), Texts.TMPColorRed));

            // so to match with regexp, we match on the prefix and the end of the line
            var curSbValue = sb.ToString();
            var pat = @$"{T.MatEnergyOutput}.*$";
            var numberPat = @"\d+(\.\d+)?";
            var match = Regex.Match(curSbValue, pat, RegexOptions.Multiline);
            if (!match.Success)
            {
                return;
            }
            // There is also a number in the color, so carefully only get the last one.
            var numberInside = Regex.Matches(match.Value, numberPat).Last();
            if (!numberInside.Success)
            {
                MoreNumbersPatcher.LogErrOnce($"For def {__instance.Id}, found an energy output section '{match.Value}' but didn't find a number in it. This error will only be reported once per def.");
                return;
            }
            var num = float.Parse(numberInside.Value);
            // this is kW*min, so divide by 60 for kWh.
            var kWh = EnergyOutputToKWH(num);
            var adjustedValue = $"{numberInside.Value} ({kWh:f1} kWh in Matter Reactor)";
            // Finally, one final careful replace
            sb.Replace(numberInside.Value, adjustedValue, match.Index + numberInside.Index, numberInside.Length);
        }
        public static float EnergyOutputToKWH(float x)
        {
            // We include the 0.25 reactor efficiency constant for convenience, so that
            // this is straightforwardly the power produced when burning in a small matter reactor at the normal 100% efficiency.
            return x / (60f / 4f);
        }
    }
    [HarmonyPatch]
    class BatteryDataPatcher
    {

        // This section handles all buildings, so we inject our battery section after it.
        [HarmonyPatch(typeof(Def), nameof(Def.AppendConstructableDesc))]
        [HarmonyPostfix]
        static void AppendConstructableDescPatch(Def __instance, StringBuilder sb, bool forBuildTool)
        {
            // for some reason the public version isn't static, so we use the private one:
            int nameH = (int)AccessTools.Field(typeof(BatteryComp), "nameHash").GetValue(typeof(BatteryComp));
            var batteryConfig = __instance.ComponentConfigFor(nameH, warn: false);
            if (batteryConfig == null)
            {
                return; // not a battery
            }
            // am I meant to just do this and pray?
            var batteryOutput = batteryConfig.GetInt(Animator.StringToHash("EnergyOutput"));

            var electricNodeConfig = __instance.ComponentConfigFor(Animator.StringToHash("ElectricNode"));
            if (electricNodeConfig == null)
            {
                MoreNumbersPatcher.LogErrOnce($"Def {__instance} has a Battery component but no ElectricNode component. Ignoring it.");
                return;
            }
            var maxCharge = electricNodeConfig.GetInt(Animator.StringToHash("BatteryMaxCharge"));
            var isBattery = electricNodeConfig.GetBool(Animator.StringToHash("IsBattery"));
            if (maxCharge > 0 && !isBattery)
            {
                MoreNumbersPatcher.LogErrOnce($"Def {__instance} has a Battery component with BatteryMaxCharge={maxCharge} but also has IsBattery={isBattery}. This is unexpected to me.");
            }

            sb.Append(Game.Input.GlyphMap.BRHR);
            sb.Append($"Acts as a {Texts.WithColor("battery", Texts.TMPColorYellow)} with capacity {(int)BatteryCapacityToKWH(maxCharge)}kWh and max output {batteryOutput}kW ({BatteryCapacityToKWH(maxCharge) / batteryOutput:f1}h to fully discharge).");
        }
        public static float BatteryCapacityToKWH(float x)
        {
            // For some reason the unit of these is 10 kW*minute (BatteryComp.EstimateRemainingTime). 
            return x / 6;
        }
    }

    [HarmonyPatch]
    class DeficitSilencePatcher
    {

        // This section handles all buildings, so we inject our battery section after it.
        [HarmonyPatch(typeof(MultiCrafterComp), nameof(MultiCrafterComp.NotifyMatDeficit))]
        [HarmonyPrefix]
        static bool NotifyMatDeficitPatch(MultiCrafterComp __instance, MatType type)
        {
            // Silence missing material notifications for unlimited orders
            if (__instance.CurrentOrder?.Demand?.Type == CraftingDemandType.Unlimited)
            {
                return false; // skip
            }
            return true;
        }
    }
}