namespace RQ3Evaluator.Wpf;
public static class ExcelExporter
{
    public static void Export(string folder,IReadOnlyList<Prediction> predictions,IReadOnlyList<(Prediction P,Gold G)> scored)
    {
        Directory.CreateDirectory(folder);
        using(var detail=new XLWorkbook())
        {
            var all=detail.AddWorksheet("All_Predictions");
            all.Cell(1,1).InsertTable(predictions.Select(p=>new {p.CaseId,p.Verifier,p.Decision,p.Cause,p.Route,p.Abstained,p.ExecutionStatus,p.LatencyMs,p.InputTokens,p.OutputTokens,p.ApiCalls,p.CostUsd,p.RequestHash,p.ResponseHash,p.Rationale}),"Predictions",true);
            all.SheetView.FreezeRows(1); all.Columns().AdjustToContents(); all.Column(15).Width=65; all.Column(15).Style.Alignment.WrapText=true;
            detail.SaveAs(Path.Combine(folder,"rq3_detailed_predictions.xlsx"));
        }
        using(var summary=new XLWorkbook())
        {
            var ws=summary.AddWorksheet("RQ3 Summary");
            ws.Cell("A1").Value="RQ3 Comparative Evaluation Summary"; ws.Range("A1:F1").Merge(); ws.Cell("A1").Style.Font.Bold=true; ws.Cell("A1").Style.Font.FontSize=16; ws.Cell("A1").Style.Font.FontColor=XLColor.FromHtml("0077B6");
            ws.Cell("A3").Value="Method"; ws.Cell("B3").Value="Decision accuracy"; ws.Cell("C3").Value="False acceptance"; ws.Cell("D3").Value="Cause accuracy"; ws.Cell("E3").Value="Route accuracy"; ws.Cell("F3").Value="Abstain";
            var header=ws.Range("A3:F3");header.Style.Fill.BackgroundColor=XLColor.FromHtml("0077B6");header.Style.Font.FontColor=XLColor.White;header.Style.Font.Bold=true;header.Style.Alignment.Horizontal=XLAlignmentHorizontalValues.Center;
            var row=4;
            foreach(var group in scored.GroupBy(x=>x.P.Verifier).OrderBy(x=>Order(x.Key)))
            {
                var a=group.ToArray();var falsePassing=a.Where(x=>x.G.FalsePassing).ToArray();var defects=a.Where(x=>x.G.Decision=="REPAIR_REQUIRED").ToArray();
                ws.Cell(row,1).Value=DisplayName(group.Key); ws.Cell(row,2).Value=Rate(a,x=>x.P.Decision==x.G.Decision); ws.Cell(row,3).Value=Rate(falsePassing,x=>x.P.Decision=="VERIFIED"); ws.Cell(row,4).Value=Rate(defects,x=>x.P.Cause==x.G.Cause); ws.Cell(row,5).Value=Rate(defects,x=>x.G.AcceptableRoutes.Contains(x.P.Route)); ws.Cell(row,6).Value=Rate(a,x=>x.P.Abstained); row++;
            }
            if(scored.Count==0){ws.Cell(4,1).Value="Scoring was not selected; no private gold metrics are available.";ws.Range("A4:F4").Merge();}
            ws.Range(4,2,Math.Max(4,row-1),6).Style.NumberFormat.Format="0.00%"; ws.Range(3,1,Math.Max(4,row-1),6).Style.Border.OutsideBorder=XLBorderStyleValues.Thin; ws.Range(3,1,Math.Max(4,row-1),6).Style.Border.InsideBorder=XLBorderStyleValues.Thin; ws.Columns().AdjustToContents(); ws.Column(1).Width=Math.Max(25,ws.Column(1).Width);
            summary.SaveAs(Path.Combine(folder,"rq3_summary.xlsx"));
        }
        File.WriteAllText(Path.Combine(folder,"predictions.json"),JsonSerializer.Serialize(predictions,new JsonSerializerOptions{WriteIndented=true}));
    }
    static double Rate<T>(IReadOnlyCollection<T> x,Func<T,bool> f)=>x.Count==0?0:Math.Round(x.Count(f)/(double)x.Count,4);
    static int Order(string name)=>name.StartsWith("B0")?0:name.StartsWith("B1")?1:2;
    static string DisplayName(string name)=>name switch {"B0_BASIC_TOOL_GATE"=>"B0 tool gate","B1_GENERAL_REVIEWER"=>"B1 general reviewer","B2_SPECIALIZED_MULTI_AGENT"=>"B2 specialized multi-agent",_=>name};
}
