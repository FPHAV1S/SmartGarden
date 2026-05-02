using IrrigationSystem.Web.Models;

namespace IrrigationSystem.Web.Services;

public static class IrrigationSafetyPolicy
{
    public static RuleBasedIrrigationDecision BuildRuleBasedDecision(
        SensorReading latestReading,
        Zone? zone,
        SystemSettings? settings,
        IReadOnlyList<IrrigationEvent> recentEvents,
        AiIrrigationOptions options,
        DateTime? now = null)
    {
        settings ??= new SystemSettings();
        var duration = Math.Clamp(settings.DefaultWateringDuration > 0
            ? settings.DefaultWateringDuration
            : options.DefaultFallbackDurationSeconds, 0, options.MaxDurationSeconds);

        if (zone == null)
        {
            return new RuleBasedIrrigationDecision
            {
                ShouldWater = false,
                RecommendedDurationSeconds = 0,
                Reason = "Rule fallback blocked watering because the zone was not found."
            };
        }

        if (!zone.IsActive)
        {
            return new RuleBasedIrrigationDecision
            {
                ShouldWater = false,
                RecommendedDurationSeconds = 0,
                Reason = $"Rule fallback blocked watering because Zone {zone.Id} is inactive."
            };
        }

        if (!settings.AutoWateringEnabled)
        {
            return new RuleBasedIrrigationDecision
            {
                ShouldWater = false,
                RecommendedDurationSeconds = 0,
                Reason = "Rule fallback blocked watering because auto-watering is disabled."
            };
        }

        var currentTime = now ?? DateTime.Now;
        if (settings.NightModeEnabled && !IsWithinNightModeWindow(settings, currentTime))
        {
            return new RuleBasedIrrigationDecision
            {
                ShouldWater = false,
                RecommendedDurationSeconds = 0,
                Reason = "Rule fallback blocked watering because night mode is outside watering hours."
            };
        }

        if (!latestReading.Moisture.HasValue)
        {
            return new RuleBasedIrrigationDecision
            {
                ShouldWater = false,
                RecommendedDurationSeconds = 0,
                Reason = "Rule fallback blocked watering because the latest moisture reading is missing."
            };
        }

        if (latestReading.Moisture.Value >= zone.MoistureThreshold)
        {
            return new RuleBasedIrrigationDecision
            {
                ShouldWater = false,
                RecommendedDurationSeconds = 0,
                Reason = $"Rule fallback does not water because moisture {latestReading.Moisture.Value:F1}% is at or above threshold {zone.MoistureThreshold:F1}%."
            };
        }

        if (IsWithinWateringCooldown(recentEvents, settings, options, currentTime, out var minutesSinceWatering, out var cooldownMinutes))
        {
            return new RuleBasedIrrigationDecision
            {
                ShouldWater = false,
                RecommendedDurationSeconds = 0,
                Reason = $"Rule fallback blocked watering because the zone watered {minutesSinceWatering:F0} minutes ago; cooldown is {cooldownMinutes:F0} minutes."
            };
        }

        return new RuleBasedIrrigationDecision
        {
            ShouldWater = true,
            RecommendedDurationSeconds = duration,
            Reason = $"Rule fallback waters because moisture {latestReading.Moisture.Value:F1}% is below threshold {zone.MoistureThreshold:F1}%."
        };
    }

    public static IrrigationDecisionResult Apply(
        SensorReading latestReading,
        Zone? zone,
        SystemSettings? settings,
        IReadOnlyList<IrrigationEvent> recentEvents,
        RuleBasedIrrigationDecision fallbackDecision,
        AiIrrigationDecision? aiDecision,
        AiIrrigationOptions options,
        bool aiWasAttempted,
        string? aiErrorMessage = null,
        DateTime? now = null)
    {
        settings ??= new SystemSettings();
        var currentTime = now ?? DateTime.Now;
        var notes = new List<string>();
        var useFallback = aiDecision == null;
        var hardBlock = false;
        var finalShouldWater = false;
        var duration = 0;
        var reason = fallbackDecision.Reason;
        var riskLevel = aiDecision?.RiskLevel ?? "LOW";
        var confidence = aiDecision?.Confidence ?? 0;
        var learnedObservation = aiDecision?.LearnedObservation ?? string.Empty;
        var suggestedThreshold = aiDecision?.SuggestedMoistureThreshold;

        if (useFallback && !string.IsNullOrWhiteSpace(aiErrorMessage))
        {
            notes.Add(aiErrorMessage);
        }

        if (zone == null)
        {
            notes.Add("Safety blocked watering because the zone was not found.");
            useFallback = true;
            hardBlock = true;
        }
        else if (!zone.IsActive)
        {
            notes.Add($"Safety blocked watering because Zone {zone.Id} is inactive.");
            useFallback = true;
            hardBlock = true;
        }
        else if (!settings.AutoWateringEnabled)
        {
            notes.Add("Safety blocked watering because auto-watering is disabled.");
            useFallback = true;
            hardBlock = true;
        }
        else if (settings.NightModeEnabled && !IsWithinNightModeWindow(settings, currentTime))
        {
            notes.Add("Safety blocked watering because night mode is outside watering hours.");
            useFallback = true;
            hardBlock = true;
        }
        else if (!latestReading.Moisture.HasValue)
        {
            notes.Add("Safety blocked watering because the latest moisture reading is missing.");
            useFallback = true;
            hardBlock = true;
        }
        else if (latestReading.Moisture.HasValue && latestReading.Moisture.Value >= options.HighMoistureBlockPercent)
        {
            notes.Add($"Safety blocked watering because moisture {latestReading.Moisture.Value:F1}% is at or above {options.HighMoistureBlockPercent:F1}%.");
            reason = "Moisture is already high, so watering is blocked.";
            hardBlock = true;
        }
        else if (aiDecision != null && string.Equals(aiDecision.RiskLevel, "HIGH", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("Safety blocked automatic watering because AI riskLevel was HIGH.");
            reason = aiDecision.Reason;
            hardBlock = true;
        }
        else if (aiDecision != null && aiDecision.Confidence < options.LowConfidenceThreshold)
        {
            notes.Add($"AI confidence {aiDecision.Confidence:F2} was below {options.LowConfidenceThreshold:F2}; using rule fallback.");
            useFallback = true;
        }

        if (!hardBlock && (notes.Count == 0 || useFallback))
        {
            if (useFallback)
            {
                finalShouldWater = fallbackDecision.ShouldWater;
                duration = Math.Clamp(fallbackDecision.RecommendedDurationSeconds, 0, options.MaxDurationSeconds);
                reason = fallbackDecision.Reason;
            }
            else if (aiDecision != null)
            {
                finalShouldWater = aiDecision.ShouldWater &&
                    string.Equals(aiDecision.RecommendedValveState, "ON", StringComparison.OrdinalIgnoreCase);
                duration = Math.Clamp(aiDecision.RecommendedDurationSeconds, 0, options.MaxDurationSeconds);
                reason = aiDecision.Reason;

                if (duration != aiDecision.RecommendedDurationSeconds)
                {
                    notes.Add($"Safety clamped watering duration from {aiDecision.RecommendedDurationSeconds}s to {duration}s.");
                }
            }
        }

        if (finalShouldWater && IsWithinWateringCooldown(recentEvents, settings, options, currentTime, out var minutesSinceWatering, out var cooldownMinutes))
        {
            finalShouldWater = false;
            duration = 0;
            notes.Add($"Safety blocked repeated watering because the zone watered {minutesSinceWatering:F0} minutes ago; cooldown is {cooldownMinutes:F0} minutes.");
        }

        if (!finalShouldWater)
        {
            duration = 0;
        }

        return new IrrigationDecisionResult
        {
            Timestamp = currentTime,
            SensorReadingId = latestReading.Id,
            ZoneId = latestReading.ZoneId,
            MoisturePercent = latestReading.Moisture,
            RuleBasedShouldWater = fallbackDecision.ShouldWater,
            RuleBasedReason = fallbackDecision.Reason,
            AiWasAttempted = aiWasAttempted,
            AiDecision = aiDecision,
            FinalShouldWaterAfterSafety = finalShouldWater,
            RecommendedValveState = finalShouldWater ? "ON" : "OFF",
            RecommendedDurationSeconds = duration,
            WasFallbackUsed = useFallback,
            ErrorMessage = aiErrorMessage,
            Reason = reason,
            LearnedObservation = learnedObservation,
            SuggestedMoistureThreshold = suggestedThreshold,
            Confidence = confidence,
            RiskLevel = riskLevel,
            SafetyNotes = string.Join(" ", notes)
        };
    }

    private static bool IsWithinNightModeWindow(SystemSettings settings, DateTime now)
    {
        var currentHour = now.Hour;

        if (settings.NightModeStartHour < settings.NightModeEndHour)
        {
            return currentHour >= settings.NightModeStartHour && currentHour < settings.NightModeEndHour;
        }

        return currentHour >= settings.NightModeStartHour || currentHour < settings.NightModeEndHour;
    }

    private static bool IsWithinWateringCooldown(
        IReadOnlyList<IrrigationEvent> recentEvents,
        SystemSettings settings,
        AiIrrigationOptions options,
        DateTime now,
        out double minutesSinceWatering,
        out double cooldownMinutes)
    {
        var lastWatering = recentEvents
            .OrderByDescending(irrigationEvent => irrigationEvent.StartedAt)
            .FirstOrDefault();

        cooldownMinutes = options.MinimumMinutesBetweenWaterings;
        if (settings.EcoModeEnabled)
        {
            cooldownMinutes *= 1.5;
        }

        if (lastWatering == null)
        {
            minutesSinceWatering = double.MaxValue;
            return false;
        }

        minutesSinceWatering = (now - lastWatering.StartedAt).TotalMinutes;
        return minutesSinceWatering < cooldownMinutes;
    }
}
