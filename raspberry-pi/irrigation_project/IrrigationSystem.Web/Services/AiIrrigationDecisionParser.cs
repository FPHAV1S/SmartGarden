using System.Text.Json;
using IrrigationSystem.Web.Models;

namespace IrrigationSystem.Web.Services;

public static class AiIrrigationDecisionParser
{
    private static readonly HashSet<string> ValveStates = new(StringComparer.OrdinalIgnoreCase) { "ON", "OFF" };
    private static readonly HashSet<string> RiskLevels = new(StringComparer.OrdinalIgnoreCase) { "LOW", "MEDIUM", "HIGH" };

    public static bool TryParse(string json, out AiIrrigationDecision decision, out string error)
    {
        decision = new AiIrrigationDecision();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "AI response was empty.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "AI response must be a JSON object.";
                return false;
            }

            if (!TryGetBoolean(root, "shouldWater", out var shouldWater, out error) ||
                !TryGetString(root, "recommendedValveState", out var valveState, out error) ||
                !TryGetInt(root, "recommendedDurationSeconds", out var duration, out error) ||
                !TryGetDouble(root, "confidence", out var confidence, out error) ||
                !TryGetString(root, "reason", out var reason, out error) ||
                !TryGetString(root, "learnedObservation", out var learnedObservation, out error) ||
                !TryGetDouble(root, "suggestedMoistureThreshold", out var suggestedThreshold, out error) ||
                !TryGetString(root, "riskLevel", out var riskLevel, out error))
            {
                return false;
            }

            valveState = valveState.ToUpperInvariant();
            riskLevel = riskLevel.ToUpperInvariant();

            if (!ValveStates.Contains(valveState))
            {
                error = "recommendedValveState must be ON or OFF.";
                return false;
            }

            if (duration is < 0 or > 120)
            {
                error = "recommendedDurationSeconds must be between 0 and 120.";
                return false;
            }

            if (confidence is < 0 or > 1)
            {
                error = "confidence must be between 0 and 1.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                error = "reason is required.";
                return false;
            }

            if (suggestedThreshold is < 0 or > 100)
            {
                error = "suggestedMoistureThreshold must be between 0 and 100.";
                return false;
            }

            if (!RiskLevels.Contains(riskLevel))
            {
                error = "riskLevel must be LOW, MEDIUM, or HIGH.";
                return false;
            }

            decision = new AiIrrigationDecision
            {
                ShouldWater = shouldWater,
                RecommendedValveState = shouldWater ? valveState : "OFF",
                RecommendedDurationSeconds = duration,
                Confidence = confidence,
                Reason = reason.Trim(),
                LearnedObservation = learnedObservation.Trim(),
                SuggestedMoistureThreshold = (float)suggestedThreshold,
                RiskLevel = riskLevel
            };

            return true;
        }
        catch (JsonException ex)
        {
            error = $"AI response was not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryGetBoolean(JsonElement root, string propertyName, out bool value, out string error)
    {
        value = false;
        error = string.Empty;

        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False)
        {
            error = $"{propertyName} must be a boolean.";
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetString(JsonElement root, string propertyName, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;

        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            error = $"{propertyName} must be a string.";
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt(JsonElement root, string propertyName, out int value, out string error)
    {
        value = 0;
        error = string.Empty;

        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out value))
        {
            error = $"{propertyName} must be an integer.";
            return false;
        }

        return true;
    }

    private static bool TryGetDouble(JsonElement root, string propertyName, out double value, out string error)
    {
        value = 0;
        error = string.Empty;

        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out value))
        {
            error = $"{propertyName} must be a number.";
            return false;
        }

        return true;
    }
}
