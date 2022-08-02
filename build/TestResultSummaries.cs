using System;
using System.Linq;
using System.Xml;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Utilities.Collections;

public class TestResultSummaries
{
    internal static ProjectCoverage GetTestCoverageSummary(AbsolutePath coverageResultFile, Project[] testProjects)
    {
        var testProjectNames = testProjects.Select(x => x.Name);

        var coverageXmlDoc = new XmlDocument();
        coverageXmlDoc.Load(coverageResultFile);
        var projectCoverages = coverageXmlDoc.SelectNodes("/Root/Assembly")
            .AsEnumerable<XmlNode>()
            .Select(x => x.Attributes.AsEnumerable<XmlAttribute>().ToDictionary(x => x.Name, x => x.Value))
            .Select(x => new ProjectCoverage
            {
                AssemblyName = x["Name"],
                CoveredStatements = x["CoveredStatements"].TryParseInt(),
                TotalStatements = x["TotalStatements"].TryParseInt(),
                CoveragePercent = x["CoveragePercent"].TryParseInt()
            })
            .Where(x => !testProjectNames.Contains(x.AssemblyName, StringComparer.InvariantCultureIgnoreCase))
            .Where(x => x.HasCoverage)
            .ToList();
        var totalCoverage = new ProjectCoverage
        {
            CoveredStatements = projectCoverages.Sum(x => x.CoveredStatements),
            TotalStatements = projectCoverages.Sum(x => x.TotalStatements),
        };
        totalCoverage.CoveragePercent =
            (int?)Math.Round(100 * totalCoverage.CoveredStatements.Value / (double)totalCoverage.TotalStatements.Value);

        return totalCoverage;
    }
    
    //(bool, int, int, int) ... I'm so sorry 😥
    //This doesn't work properly because it picks up all .trx files in the tests/results directory, including those from previous runs
    //Needs to be fixed so it only rolls up the per-project results from the _current_ test run
    internal static ProjectTestResult GetTestResultSummaryCounters(AbsolutePath testResultsLocation)
    {
        var testResultTrxs = testResultsLocation.GlobFiles("*.trx");
        var counters = testResultTrxs.Select(x =>
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.Load(x);
                return xmlDoc
                    //The System.Xml classes are _very_ namespace-aware, so you either have to provide a mapping
                    //via XmlNamespaceManager for the exact namespace in the document, or use an xpath hack
                    //like [name()='ElementName']
                    //The TRX files that xunit generate have namespace
                    //http://microsoft.com/schemas/VisualStudio/TeamTest/2010, but depending on that specific
                    //namespace seems kinda brittle
                    .SelectNodes("//*[name()='TestRun']/*[name()='ResultSummary']/*[name()='Counters']/@*")
                    .AsEnumerable<XmlNode>()
                    .Where(x => x.NodeType == XmlNodeType.Attribute)
                    .ToDictionary(
                        x => ((XmlAttribute)x).Name,
                        x =>
                        {
                            var v = ((XmlAttribute)x).Value;
                            if (int.TryParse(v, out var i)) return (int?)i;
                            return null;
                        });
            })
            .ToList();

        var results = new ProjectTestResult
        {
            TotalTests = counters.Sum(x => x["total"]),
            Passed = counters.Sum(x => x["passed"]),
            Failed = counters.Sum(x => x["failed"])
        };

        return results;
    }

    internal class ProjectCoverage
    {
        public string AssemblyName { get; set; }
        public int? CoveredStatements { get; set; }
        public int? TotalStatements { get; set; }
        public int? CoveragePercent { get; set; }

        public bool HasCoverage =>
            CoveredStatements.HasValue && TotalStatements.HasValue && CoveragePercent.HasValue;
    }

    internal class ProjectTestResult
    {
        public int? TotalTests { get; set; }
        public int? Passed { get; set; }
        public int? Failed { get; set; }
        public bool HasResults => TotalTests.HasValue && Passed.HasValue && Failed.HasValue;
    }
}