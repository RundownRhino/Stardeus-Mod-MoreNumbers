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

        public static string ExtraPFData()
        {
            // duplicating calculations from ShipSys.OnTick
            var ShipMass = A.S.Sys.Areas.TotalAreaSize;
            var engineForce = A.S.Sys.ShipNav.EnginePush();
            float shipSpeedMin = Tunables.ShipSpeedMin;
            float shipSpeedMax = Tunables.ShipSpeedMax;
            var trueVelocity = Equations.EnginePush(engineForce, ShipMass, shipSpeedMin, shipSpeedMax);
            // from Equations.EnginePush
            float rawAccel = engineForce / (float)ShipMass;
            float adjustedAccel = math.sqrt(rawAccel);
            var expectedVelocity = math.lerp(shipSpeedMin, shipSpeedMax, Maths.Clamp(adjustedAccel, 0f, 1f));
            List<string> parts = new()
            {
                $"Ship mass: {ShipMass} tiles",
                $"Raw engine force: {engineForce} kN",
                $"Raw acceleration: {rawAccel} kN/tile",
                $"Speed factor (sqrt of acceleration): {adjustedAccel}",
                $"Speed range: {shipSpeedMin} to {shipSpeedMax}",
                $"Final speed: {trueVelocity}"
            };
            if (math.abs(expectedVelocity - trueVelocity) > 1e-6)
            {
                LogErrOnce($"The velocity calculation doesn't match - probably the game changed and the mod needs to be updated. Expected to get {expectedVelocity} but game returned {trueVelocity}.");
                parts.Append($"Warning: velocity calculation doesn't match! The mod must be outdated.");
            }
            if (trueVelocity == shipSpeedMax)
            {
                parts.Append($"You have hit the ship velocity upper limit.");
            }
            return string.Join("\n", parts);

        }
    }
}