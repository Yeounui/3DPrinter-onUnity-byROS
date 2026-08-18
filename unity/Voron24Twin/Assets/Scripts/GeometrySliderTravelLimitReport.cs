using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class GeometrySliderTravelLimitReport : MonoBehaviour
{
    [Header("Report File")]
    public string reportDirectory = "Assets";
    public string reportFileName = "GeometrySliderTravelClamp_Report.md";
    public bool clearOnPlaySession = true;

    public string DefaultReportDirectory =>
        NormalizePath(Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Assets"));

    public string GetResolvedReportPath()
    {
        string directory = string.IsNullOrWhiteSpace(reportDirectory)
            ? Application.persistentDataPath
            : reportDirectory;

        if (!Path.IsPathRooted(directory))
            directory = Path.Combine(Directory.GetParent(Application.dataPath).FullName, directory);

        string fileName = string.IsNullOrWhiteSpace(reportFileName)
            ? "GeometrySliderTravelClamp_Report.md"
            : reportFileName;

        string combinedPath = Path.Combine(directory, fileName);
        return NormalizePath(combinedPath);
    }

    private static string NormalizePath(string path)
    {
        char separator = Path.DirectorySeparatorChar;
        return path
            .Replace('/', separator)
            .Replace('\\', separator);
    }

    private readonly HashSet<string> registeredParts = new HashSet<string>();
    private bool sessionInitialized;
    private string reportPath;

    public bool RegisterPart(
        string partName,
        float railMin,
        float railMax,
        float blockMin,
        float blockMax,
        Vector3 axis)
    {
        if (string.IsNullOrWhiteSpace(partName) ||
            registeredParts.Contains(partName))
            return false;

        if (!InitializeSession())
            return false;

        float blockWidth = blockMax - blockMin;
        float allowedMin = railMin;
        float allowedMax = railMax - blockWidth;
        bool valid = blockMin >= railMin && blockMax <= railMax;

        string section =
            $"## {partName}\n\n" +
            $"- Axis: `{axis}`\n" +
            $"- Rail range: `{railMin:F3} ~ {railMax:F3}`\n" +
            $"- Block range: `{blockMin:F3} ~ {blockMax:F3}`\n" +
            $"- Block width: `{blockWidth:F3}`\n" +
            $"- Allowed Block Min coordinate: `{allowedMin:F3} ~ {allowedMax:F3}`\n" +
            $"- Current state: **{(valid ? "Valid" : "Out of range")}**\n\n";

        try
        {
            File.AppendAllText(reportPath, section);
            registeredParts.Add(partName);
            Debug.Log($"[GeometrySliderTravelLimitReport] {partName}: 범위 1회 기록 완료 {reportPath}", this);
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[GeometrySliderTravelLimitReport] 기록 실패 {exception.Message}", this);
            return false;
        }
    }

    private bool InitializeSession()
    {
        if (sessionInitialized)
            return true;

        reportPath = GetResolvedReportPath();
        string directory = Path.GetDirectoryName(reportPath);

        try
        {
            Directory.CreateDirectory(directory);
            if (clearOnPlaySession || !File.Exists(reportPath))
                File.WriteAllText(reportPath, $"# Geometry Slider Travel Limit Report\n\nSession: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n");
            sessionInitialized = true;
            return true;
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"[GeometrySliderTravelLimitReport] 초기화 실패 {exception.Message}", this);
            return false;
        }
    }
}
