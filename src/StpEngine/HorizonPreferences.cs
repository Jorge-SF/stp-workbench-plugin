using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Maptek.Vulcan.Stp.Engine.Internal;

namespace Maptek.Vulcan.Stp.Engine
{
    /// <summary>
    /// Key/value store for the horizon preference (the browser used <c>localStorage</c>).
    /// Both members may throw; the engine then falls back exactly like the JS.
    /// </summary>
    public interface IHorizonStorage
    {
        /// <summary>Reads a value.</summary>
        /// <param name="key">The key.</param>
        /// <returns>The stored text, or <c>null</c> when absent.</returns>
        string GetItem(string key);

        /// <summary>Writes a value.</summary>
        /// <param name="key">The key.</param>
        /// <param name="value">The text to store.</param>
        void SetItem(string key, string value);
    }

    /// <summary>A loaded horizon preference. JS: return values of <c>loadHorizonPreference</c>, L116-133.</summary>
    public sealed class HorizonPreference
    {
        /// <summary>Gets the preference version (always 3 after loading).</summary>
        public int Version { get; init; }

        /// <summary>Gets the start day <c>YYYY-MM-DD</c>, or <c>null</c> to derive it from pending work.</summary>
        public string StartDay { get; init; }

        /// <summary>Gets the duration in days.</summary>
        public int DurationDays { get; init; }

        /// <summary>Gets a value indicating whether a version-2 preference was migrated.</summary>
        public bool Migrated { get; init; }

        /// <summary>Gets the warning text, or empty.</summary>
        public string Warning { get; init; }
    }

    /// <summary>Result of <see cref="HorizonPreferences.Save"/>. JS: return values of <c>saveHorizonPreference</c>, L135-144.</summary>
    public sealed class HorizonPreferenceSaveResult
    {
        /// <summary>Gets a value indicating whether the preference was written.</summary>
        public bool Persisted { get; init; }

        /// <summary>Gets the start day.</summary>
        public string StartDay { get; init; }

        /// <summary>Gets the duration in days.</summary>
        public int DurationDays { get; init; }

        /// <summary>Gets the warning text, or empty.</summary>
        public string Warning { get; init; }
    }

    /// <summary>Horizon preference persistence (<c>vgs-stp-v2:horizon:&lt;projectKey&gt;</c>, versions 2 and 3). Port of planner_logic.js L65-154.</summary>
    public static class HorizonPreferences
    {
        /// <summary>JS: <c>MAX_HORIZON_PREFERENCE_BYTES</c>, L8.</summary>
        public const int MaxPreferenceBytes = 1024;

        private const string ReadWarning = "No se pudo leer la preferencia de horizonte.";
        private const string InvalidWarning = "Preferencia de horizonte inválida; se usarán 28 días.";
        private const string SaveWarning = "No se pudo guardar la preferencia de horizonte.";
        private static readonly Regex ControlCharacters = new Regex("[\u0000-\u001f\u007f]", RegexOptions.CultureInvariant);
        private static readonly Regex DayPattern = new Regex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant);

        /// <summary>The storage key for a project. JS: <c>horizonStorageKey</c>, L65-68.</summary>
        /// <param name="projectKey">The project key.</param>
        /// <returns><c>vgs-stp-v2:horizon:&lt;projectKey&gt;</c>.</returns>
        /// <exception cref="EngineException"><c>clave de proyecto inválida</c>.</exception>
        public static string StorageKey(string projectKey)
        {
            if (string.IsNullOrEmpty(projectKey) || Js.Trim(projectKey) != projectKey || projectKey.Length > 256 || ControlCharacters.IsMatch(projectKey))
            {
                throw EngineException.Fail("clave de proyecto inválida");
            }

            return "vgs-stp-v2:horizon:" + projectKey;
        }

        /// <summary>JS: <c>horizonPreferenceFallback</c>, L69.</summary>
        /// <param name="warning">The warning.</param>
        /// <returns>28 days, no start day.</returns>
        public static HorizonPreference Fallback(string warning = "") =>
            new HorizonPreference { Version = 3, StartDay = null, DurationDays = 28, Migrated = false, Warning = warning };

        /// <summary>Loads and validates the preference; any problem yields the 28-day fallback. JS: <c>loadHorizonPreference</c>, L116-133.</summary>
        /// <param name="storage">The storage.</param>
        /// <param name="projectKey">The project key.</param>
        /// <returns>The preference.</returns>
        public static HorizonPreference Load(IHorizonStorage storage, string projectKey)
        {
            string key;
            try
            {
                key = StorageKey(projectKey);
            }
            catch (EngineException)
            {
                return Fallback(ReadWarning);
            }

            string raw;
            try
            {
                raw = storage.GetItem(key);
            }
            catch (Exception)
            {
                return Fallback(ReadWarning);
            }

            if (raw == null)
            {
                return Fallback();
            }

            try
            {
                if (Encoding.UTF8.GetByteCount(raw) > MaxPreferenceBytes)
                {
                    throw new FormatException("invalid");
                }

                if (!(MiniJson.Parse(raw) is Dictionary<string, object> parsed)
                    || !TryInteger(parsed, "duration_days", out double duration) || duration < 1 || duration > 366)
                {
                    throw new FormatException("invalid");
                }

                string keys = string.Join(",", parsed.Keys.OrderBy(k => k, StringComparer.Ordinal));
                bool hasVersion = parsed.TryGetValue("version", out object version) && version is double;
                double versionNumber = hasVersion ? (double)version : double.NaN;
                if (versionNumber == 2 && keys == "duration_days,version")
                {
                    return new HorizonPreference { Version = 3, StartDay = null, DurationDays = (int)duration, Migrated = true, Warning = string.Empty };
                }

                if (versionNumber == 3 && keys == "duration_days,start_day,version")
                {
                    return new HorizonPreference { Version = 3, StartDay = ParseStartDay(parsed["start_day"]), DurationDays = (int)duration, Migrated = false, Warning = string.Empty };
                }

                throw new FormatException("invalid");
            }
            catch (Exception ex) when (ex is FormatException || ex is EngineException)
            {
                return Fallback(InvalidWarning);
            }
        }

        /// <summary>Validates and stores the horizon as a version-3 preference. JS: <c>saveHorizonPreference</c>, L135-144.</summary>
        /// <param name="storage">The storage.</param>
        /// <param name="projectKey">The project key.</param>
        /// <param name="start">Horizon start (midnight).</param>
        /// <param name="finish">Horizon finish (midnight).</param>
        /// <param name="durationDays">Duration; must equal the day count.</param>
        /// <returns>The result; never throws for storage failures.</returns>
        /// <exception cref="EngineException"><c>preferencia de horizonte inválida</c> and similar for an invalid horizon.</exception>
        public static HorizonPreferenceSaveResult Save(IHorizonStorage storage, string projectKey, string start, string finish, long durationDays)
        {
            // JS: normalizeHorizonPreference, L76-82 (the key-shape check is implied by the typed signature).
            string startText = CivilTime.MidnightText(start), finishText = CivilTime.MidnightText(finish);
            int duration = Rules.DurationDays(durationDays);
            if (CivilTime.DayCount(startText, finishText) != duration)
            {
                throw EngineException.Fail("preferencia de horizonte inválida");
            }

            string startDay = startText.Substring(0, 10);
            string key;
            try
            {
                key = StorageKey(projectKey);
            }
            catch (EngineException)
            {
                return new HorizonPreferenceSaveResult { Persisted = false, StartDay = startDay, DurationDays = duration, Warning = SaveWarning };
            }

            try
            {
                storage.SetItem(key, "{\"version\":3,\"start_day\":\"" + startDay + "\",\"duration_days\":" + duration.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}");
                return new HorizonPreferenceSaveResult { Persisted = true, StartDay = startDay, DurationDays = duration, Warning = string.Empty };
            }
            catch (Exception)
            {
                return new HorizonPreferenceSaveResult { Persisted = false, StartDay = startDay, DurationDays = duration, Warning = SaveWarning };
            }
        }

        /// <summary>
        /// Returns a usable storage, or one whose members always throw. JS: <c>acquireHorizonStorage</c>, L146-154.
        /// </summary>
        /// <param name="readStorage">Factory for the storage (may throw or return <c>null</c>).</param>
        /// <returns>The storage.</returns>
        public static IHorizonStorage Acquire(Func<IHorizonStorage> readStorage)
        {
            try
            {
                return readStorage?.Invoke() ?? new UnavailableStorage();
            }
            catch (Exception)
            {
                return new UnavailableStorage();
            }
        }

        private static bool TryInteger(Dictionary<string, object> parsed, string key, out double value)
        {
            value = 0;
            if (!parsed.TryGetValue(key, out object raw) || !(raw is double number) || double.IsInfinity(number) || double.IsNaN(number) || Math.Floor(number) != number)
            {
                return false;
            }

            value = number;
            return true;
        }

        // JS: parsePreferenceStartDay, L71-75.
        private static string ParseStartDay(object value)
        {
            if (!(value is string text) || !DayPattern.IsMatch(text))
            {
                throw EngineException.Fail("preferencia inválida");
            }

            return CivilTime.MidnightText(text + "T00:00:00").Substring(0, 10);
        }

        private sealed class UnavailableStorage : IHorizonStorage
        {
            public string GetItem(string key) => throw new InvalidOperationException("storage unavailable");

            public void SetItem(string key, string value) => throw new InvalidOperationException("storage unavailable");
        }
    }
}
