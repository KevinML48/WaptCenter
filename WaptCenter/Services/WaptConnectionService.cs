using WaptCenter.Models;

namespace WaptCenter.Services;

public sealed class WaptConnectionService
{
    private const string FixedPackageFilterValue = "cd48";
    private readonly WaptBridgePackageService _waptBridgePackageService;

    public WaptConnectionService(WaptBridgePackageService waptBridgePackageService)
    {
        _waptBridgePackageService = waptBridgePackageService;
    }

    public async Task<WaptConnectionTestResult> TestConnectionAsync(
        WaptConfig? config,
        CancellationToken cancellationToken = default)
    {
        if (config is null)
        {
            return Fail("Configuration absente.");
        }

        try
        {
            var packages = await _waptBridgePackageService.GetCd48PackagesAsync(config, cancellationToken);
            var technicalDetails = BuildBridgeTechnicalDetails(
                bridgeTechnicalDetails: _waptBridgePackageService.LastTechnicalDetails,
                bridgeState: "OK",
                visiblePackageCount: packages.Count);
            var selectedStrategy = ExtractLastTechnicalValue(technicalDetails, "Selected strategy:");
            var totalVisiblePackages = ExtractLastTechnicalValue(technicalDetails, "Total packages before filter:");

            return new WaptConnectionTestResult
            {
                Success = true,
                Message = BuildSuccessMessage(selectedStrategy, totalVisiblePackages, packages.Count),
                TechnicalDetails = technicalDetails
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail(
                "Le test du bridge WAPT a expire (timeout).",
                BuildBridgeTechnicalDetails(
                    bridgeTechnicalDetails: _waptBridgePackageService.LastTechnicalDetails,
                    bridgeState: "Timeout",
                    visiblePackageCount: null));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fail(
                string.IsNullOrWhiteSpace(exception.Message)
                    ? "Le bridge Python WAPT a echoue."
                    : exception.Message,
                BuildBridgeTechnicalDetails(
                    bridgeTechnicalDetails: _waptBridgePackageService.LastTechnicalDetails,
                    bridgeState: "Failed",
                    visiblePackageCount: null,
                    exception: exception));
        }
    }

    private static string BuildSuccessMessage(
        string? selectedStrategy,
        string? totalPackagesBeforeFilter,
        int visiblePackageCount)
    {
        var strategySegment = string.IsNullOrWhiteSpace(selectedStrategy)
            ? ""
            : $" Strategie retenue: {selectedStrategy}.";
        var totalSegment = string.IsNullOrWhiteSpace(totalPackagesBeforeFilter)
            ? ""
            : $" {totalPackagesBeforeFilter} paquet(s) visibles avant filtrage.";

        if (visiblePackageCount > 0)
        {
            return $"Bridge Python WAPT operationnel.{strategySegment}{totalSegment} {visiblePackageCount} paquet(s) dont package_id contient '{FixedPackageFilterValue}' visible(s) pour l'application.".Trim();
        }

        return $"Bridge Python WAPT operationnel.{strategySegment}{totalSegment} Aucun paquet dont package_id contient '{FixedPackageFilterValue}' n'est visible sur le flux actuel, mais le bridge a repondu correctement.".Trim();
    }

    private static string BuildBridgeTechnicalDetails(
        string? bridgeTechnicalDetails,
        string bridgeState,
        int? visiblePackageCount,
        Exception? exception = null)
    {
        var lines = new List<string>
        {
            "Validation flow: Python bridge WAPT",
            $"Bridge state: {bridgeState}",
            "Filter field used: package_id",
            "Filter mode used: contains",
            $"Filter value used: {FixedPackageFilterValue}"
        };

        var selectedStrategy = ExtractLastTechnicalValue(bridgeTechnicalDetails, "Selected strategy:");
        if (!string.IsNullOrWhiteSpace(selectedStrategy))
        {
            lines.Add($"Selected strategy: {selectedStrategy}");
        }

        var totalPackagesBeforeFilter = ExtractLastTechnicalValue(bridgeTechnicalDetails, "Total packages before filter:");
        if (!string.IsNullOrWhiteSpace(totalPackagesBeforeFilter))
        {
            lines.Add($"Total packages before filter: {totalPackagesBeforeFilter}");
        }

        var matchedPackages = ExtractLastTechnicalValue(
            bridgeTechnicalDetails,
            "Total packages matching filter:");
        if (!string.IsNullOrWhiteSpace(matchedPackages))
        {
            lines.Add($"Visible packages matching filter: {matchedPackages}");
        }
        else if (visiblePackageCount is not null)
        {
            lines.Add($"Visible packages matching filter: {visiblePackageCount.Value}");
        }

        var firstMatchingPackageIds = ExtractLastTechnicalValue(bridgeTechnicalDetails, "First matching package_ids:");
        if (!string.IsNullOrWhiteSpace(firstMatchingPackageIds))
        {
            lines.Add($"First matching package_ids: {firstMatchingPackageIds}");
        }

        if (exception is not null)
        {
            lines.Add($"Bridge error: {exception.Message}");
        }

        return CombineTechnicalDetails(
            string.Join(Environment.NewLine, lines),
            NullIfWhiteSpace(bridgeTechnicalDetails),
            exception?.ToString()) ?? string.Empty;
    }

    private static string? ExtractLastTechnicalValue(string? technicalDetails, string label)
    {
        if (string.IsNullOrWhiteSpace(technicalDetails))
        {
            return null;
        }

        var lines = technicalDetails
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Reverse();

        foreach (var line in lines)
        {
            if (!line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line[label.Length..].Trim();
        }

        return null;
    }

    private static string? CombineTechnicalDetails(params string?[] detailSections)
    {
        var normalizedSections = detailSections
            .Select(section => NullIfWhiteSpace(section))
            .Where(section => section is not null)
            .Cast<string>()
            .ToList();

        return normalizedSections.Count == 0
            ? null
            : string.Join($"{Environment.NewLine}{Environment.NewLine}", normalizedSections);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static WaptConnectionTestResult Fail(string message, string? technicalDetails = null)
    {
        return new WaptConnectionTestResult
        {
            Success = false,
            Message = message,
            TechnicalDetails = NullIfWhiteSpace(technicalDetails)
        };
    }
}