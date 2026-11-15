namespace Knowledge;

public record Finding(string FindingId,string Kind,int Priority,string Summary,string Status,object Statistics,
    EvidenceRef[] Supporting,EvidenceRef[] Contradictory,string[] Limitations,string NextStep);
public record AnalysisReport(string Version,string Status,int Samples,int Series,int ComparisonsAttempted,
    int FindingsTotal,Finding[] Findings,object[] Coverage,string[] Limitations,object Presentation);

// Pure deterministic analysis. No model calls, stored diagnoses, treatment writes or inferred causal effects.
public static class HealthAnalysis
{
    public const string Version="investigation-1";
    static readonly string[] Observational=["Exploratory observational evidence; not a causal or diagnostic conclusion.",
        "Multiple comparisons and serial dependence can create chance associations. No statistical significance or clinical validation is claimed.",
        "Unmeasured confounding, selective recording and changing device behavior remain possible."];
    static EvidenceRef[] Refs(IEnumerable<CleanSample> samples)=>samples.DistinctBy(s=>(s.Source.ResourceId,s.Source.Key,s.Source.Metric)).Take(8).Select(s=>s.Evidence).ToArray();
    static Finding Make(string kind,HealthSeries a,int priority,string summary,object statistics,IEnumerable<CleanSample> support,
        IEnumerable<CleanSample>? contradict=null,string[]? limitations=null,string next="Inspect source evidence and discuss consequential repeated patterns with the appropriate clinician.")
    {
        var refs=Refs(support);
        var id=Hashing.Hash(Version+kind+a.Id+System.Text.Json.JsonSerializer.Serialize(statistics)+string.Join('|',refs.Select(x=>x.Revision)))[..20];
        return new(id,kind,priority,summary,"observed",statistics,refs,Refs(contradict??[]),limitations??[],next);
    }
    public static AnalysisReport Explore(HealthSample[] raw,DateOnly from,DateOnly until,int limit=3)
    {
        var series=HealthData.Prepare(raw);var findings=new List<Finding>();int comparisons=0;
        foreach(var s in series)
        {
            var conflicts=s.Samples.Where(x=>x.Conflict).ToArray();
            if(conflicts.Length>0)findings.Add(Make("conflicting_evidence",s,90,$"{s.Metric}: overlapping exports disagree; conflicting observations excluded from exact calculations.",new{count=conflicts.Length},conflicts.SelectMany(x=>x.Originals.Select(o=>new CleanSample(o,HealthSemantics.Canonical(o.Metric,o.Value,o.Unit),true,1,[o]))),
                limitations:["The chosen representative is traceable; it is not adjudicated as the true value."],next:"Review overlapping originals before interpreting affected periods."));
            if(s.Meaning is null)continue;
            // Clinical bands are a small versioned policy, separate from generic discovery algorithms.
            if(s.Meaning.Low is double low)Band(low,true,100);
            if(s.Meaning.High is double high)Band(high,false,80);
            void Band(double threshold,bool lower,int priority)
            {
                var matched=s.Samples.Where(x=>!x.Conflict && x.Value is not null && (x.Exact ||
                    HealthSemantics.Quality(x.Source.Quality,"comparison")== (lower?"<":">")))
                    .Where(x=>x.Exact?(lower?x.Value<threshold:x.Value>threshold):(lower?x.Value<=threshold:x.Value>=threshold)).ToArray();
                if(matched.Length==0)return;
                findings.Add(Make("safety_band",s,priority,$"{s.Metric}: historical readings {(lower?"below":"above")} the review band; this is not a current-status or episode-severity diagnosis.",
                    new{threshold,unit=s.Unit,comparison=lower?"<":">",observations=matched.Length,days=matched.Select(x=>x.Source.Date).Distinct().Count(),guidance=s.Meaning.Guidance},matched,
                    limitations:["Counts are observations, not episode duration or time in range. Reporting bounds are not exact values.","Clinical importance requires context and source review; historical results do not establish a current emergency."],
                    next:"Prioritize source review and a discussion with the care team; assess current symptoms/readings separately. Do not change medication or device settings from this result."));
            }
            if(s.Days.Length>=7)
            {
                var median=HealthData.Median(s.Days.Select(d=>d.Value));var mad=HealthData.Median(s.Days.Select(d=>Math.Abs(d.Value-median)));
                var outliers=s.Days.Where(d=>Math.Abs(d.Value-median)>Math.Max(4.5*1.4826*mad,Math.Abs(median)*0.1+1e-9)).OrderByDescending(d=>Math.Abs(d.Value-median)).ToArray();
                if(outliers.Length>0)findings.Add(Make("unusual_days",s,55,$"{s.Metric}: unusual daily observations relative to this person's available baseline.",
                    new{baselineMedian=median,mad,unusualDays=outliers.Length,totalDays=s.Days.Length,aggregation=s.Meaning.Aggregation},outliers.SelectMany(d=>d.Evidence),
                    s.Days.Where(d=>!outliers.Contains(d)).Take(2).SelectMany(d=>d.Evidence),["A robust statistical outlier is not necessarily a clinically abnormal result.","Baseline covers only observed days; sparse or selective recording may explain the difference."]));
            }
            if(s.Days.Length>=14)
            {
                var midpoint=from.AddDays((until.DayNumber-from.DayNumber+1)/2);var first=s.Days.Where(d=>d.Date<midpoint).ToArray();var last=s.Days.Where(d=>d.Date>=midpoint).ToArray();
                if(first.Length>=7 && last.Length>=7)
                {
                    comparisons++;var before=HealthData.Median(first.Select(d=>d.Value));var after=HealthData.Median(last.Select(d=>d.Value));
                    var mad=HealthData.Median(first.Select(d=>Math.Abs(d.Value-before)));var difference=after-before;
                    if(Math.Abs(difference)>Math.Max(2*1.4826*mad,Math.Abs(before)*0.2+1e-9))
                        findings.Add(Make("period_change",s,60,$"{s.Metric}: the later calendar period differs from the earlier period.",
                            new{beforeMedian=before,afterMedian=after,difference,firstDays=first.Length,lastDays=last.Length,midpoint},last.SelectMany(d=>d.Evidence),first.SelectMany(d=>d.Evidence),Observational));
                }
            }
        }
        for(int i=0;i<series.Length;i++)for(int j=i+1;j<series.Length;j++)
        {
            var a=series[i];var b=series[j];if(a.Meaning is null||b.Meaning is null)continue;
            comparisons++;
            var pair=Pair(a,b,0);
            // Pooled association can hide opposing relationships. Surface instability even
            // when the aggregate correlation is small, without naming a favored metric pair.
            if(pair.Status=="evaluated" && pair.Pairs.Length>=14)
            {
                var half=pair.Pairs.Length/2;
                double? Correlate(IEnumerable<PairPoint> points)=>HealthData.Correlation(points.Select(p=>p.A.Value).ToArray(),points.Select(p=>p.B.Value).ToArray());
                var early=Correlate(pair.Pairs.Take(half));var late=Correlate(pair.Pairs.Skip(half));
                if(early is double er && late is double lr && er*lr<0 && Math.Abs(er)>=0.6 && Math.Abs(lr)>=0.6)
                    findings.Add(Make("relationship_change",a,65,$"{a.Metric} and {b.Metric}: the observed relationship reverses direction across periods; a pooled average can conceal it.",pair.Summary,
                        pair.Pairs.Take(half).Take(4).SelectMany(p=>p.A.Evidence.Concat(p.B.Evidence)),
                        pair.Pairs.Skip(half).Take(4).SelectMany(p=>p.A.Evidence.Concat(p.B.Evidence)),Observational,
                        "Compare the two periods and source context with evaluate_health_hypothesis; investigate changing circumstances before proposing an explanation."));
            }
            if(pair.Status=="evaluated" && pair.Correlation is double r && Math.Abs(r)>=0.6 && pair.Pairs.Length>=10)
                findings.Add(Make("association",a,45,$"{a.Metric} and {b.Metric} co-vary on overlapping observed dates.",pair.Summary,
                    pair.Pairs.OrderByDescending(p=>Math.Abs(p.A.Value-pair.MedianA)*Math.Abs(p.B.Value-pair.MedianB)).SelectMany(p=>p.A.Evidence.Concat(p.B.Evidence)),
                    pair.Pairs.Where(p=>(p.A.Value-pair.MedianA)*(p.B.Value-pair.MedianB)*r<0).SelectMany(p=>p.A.Evidence.Concat(p.B.Evidence)),Observational,
                    "Test a specific hypothesis with evaluate_health_hypothesis; examine counterexamples, calendar strata, and first differences before explaining it."));
        }
        var coverage=series.Select(s=>(object)new{s.Id,s.Provider,s.Metric,s.Unit,s.Zone,semantics=s.Meaning,
            observations=s.Samples.Length,exact=s.Samples.Count(x=>x.Exact),conflicts=s.Samples.Count(x=>x.Conflict),duplicates=s.Samples.Sum(x=>x.Copies-1),
            censored=s.Samples.Count(x=>HealthSemantics.Quality(x.Source.Quality,"comparison") is not null),unknownUnits=s.Samples.Count(x=>x.Value is null),
            days=s.Days.Length,requestedDays=until.DayNumber-from.DayNumber+1,first=s.Samples.Min(x=>x.Source.Date),last=s.Samples.Max(x=>x.Source.Date),
            knownInstants=s.Samples.Count(x=>x.Source.At is not null),latestImport=s.Samples.Max(x=>x.Source.Imported),latestSourceModified=s.Samples.Max(x=>x.Source.SourceUpdated),
            missingDaysAreNotZero=true}).ToArray();
        return new(Version,raw.Length==0?"no_available_measurements":"evaluated",raw.Length,series.Length,comparisons,findings.Count,
            findings.OrderByDescending(f=>f.Priority).ThenBy(f=>f.FindingId,StringComparer.Ordinal).Take(limit).ToArray(),coverage,
            [..Observational,series.Any(s=>s.Zone is null or "unspecified")?"Some source timezones are unverified: those series cannot be aligned across sources.":"Calendar comparisons use the source-configured timezone; travel or device clock changes may require further verification.",
                "Missing days and source dates are not inferred from import time. Cite supporting resourceId values, never findingId as an original source.",
                "Evidence lists are representative (up to eight references); use hypothesis/source tools for further evidence. Medical documents require AI/source review; absence of numerical findings is not a clean bill of health."],
            new{overallAssessment="State what the evidence supports and its main limit.",maximumFindings=3,targetWords="150–250",nextSteps="Two or three practical questions; detailed evidence on request."});
    }
    public record PairPoint(DayPoint A,DayPoint B);
    public record PairResult(string Status,string? Reason,double? Correlation,double MedianA,double MedianB,PairPoint[] Pairs,object Summary);
    public static PairResult Pair(HealthSeries a,HealthSeries b,int lagDays)
    {
        if(!HealthData.CalendarCompatible(a,b))return new("cannot_evaluate","Source calendar timezones are not verified compatible.",null,0,0,[],new{a=a.Id,b=b.Id});
        var lookup=b.Days.ToDictionary(d=>d.Date);var pairs=a.Days.Where(d=>lookup.ContainsKey(d.Date.AddDays(lagDays))).Select(d=>new PairPoint(d,lookup[d.Date.AddDays(lagDays)])).ToArray();
        if(pairs.Length<7)return new("cannot_evaluate","At least seven paired observed days required; missing days are not controls.",null,0,0,pairs,new{pairedDays=pairs.Length});
        var x=pairs.Select(p=>p.A.Value).ToArray();var y=pairs.Select(p=>p.B.Value).ToArray();
        var correlation=HealthData.Correlation(x,y);var ma=HealthData.Median(x);var mb=HealthData.Median(y);
        var split=pairs.Length/2;var first=HealthData.Correlation(x.Take(split).ToArray(),y.Take(split).ToArray());var second=HealthData.Correlation(x.Skip(split).ToArray(),y.Skip(split).ToArray());
        var adjacent=pairs.Zip(pairs.Skip(1)).Where(p=>p.Second.A.Date.DayNumber-p.First.A.Date.DayNumber==1).ToArray();
        var differenced=HealthData.Correlation(adjacent.Select(p=>p.Second.A.Value-p.First.A.Value).ToArray(),adjacent.Select(p=>p.Second.B.Value-p.First.B.Value).ToArray());
        var strata=pairs.GroupBy(p=>p.A.Date.DayOfWeek).Select(g=>new{weekday=g.Key.ToString(),days=g.Count(),correlation=HealthData.Correlation(g.Select(p=>p.A.Value).ToArray(),g.Select(p=>p.B.Value).ToArray())}).ToArray();
        var hi=pairs.Where(p=>p.A.Value>ma).ToArray();var lo=pairs.Where(p=>p.A.Value<=ma).ToArray();
        return new("evaluated",null,correlation,ma,mb,pairs,new{exposure=a.Id,outcome=b.Id,lagDays,pairedDays=pairs.Length,correlation,correlationUnavailableReason=correlation is null?"Constant or insufficient variation":null,
            higherExposureOutcomeMean=hi.Length>0?(double?)hi.Average(p=>p.B.Value):null,lowerExposureOutcomeMean=lo.Length>0?(double?)lo.Average(p=>p.B.Value):null,
            higherDays=hi.Length,lowerDays=lo.Length,firstHalfCorrelation=first,secondHalfCorrelation=second,firstDifferenceCorrelation=differenced,weekdayStrata=strata,
            counterexampleDays=pairs.Count(p=>(p.A.Value-ma)*(p.B.Value-mb)*(correlation??0)<0),
            significance="Not estimated: exploratory, dependent samples and multiple comparisons. Replication and clinical review required.",
            inference="Temporal ordering and association do not establish causation."});
    }
    public static object Evaluate(HealthSample[] raw,string exposure,string outcome,string mode,int lagDays,int windowMinutes,int delayMinutes)
    {
        var series=HealthData.Prepare(raw);var a=series.Where(s=>s.Id==exposure||s.Metric==exposure).ToArray();var b=series.Where(s=>s.Id==outcome||s.Metric==outcome).ToArray();
        if(a.Length!=1||b.Length!=1)return new{status="cannot_evaluate",reason="Select one available series ID for each metric; missing or ambiguous series.",available=series.Select(s=>s.Id)};
        if(a[0].Meaning is null||b[0].Meaning is null)return new{status="cannot_evaluate",reason="Metric semantics not registered; inspect original fields before defining an adapter."};
        if(a[0].Id==b[0].Id)return new{status="cannot_evaluate",reason="Exposure and outcome must be different series."};
        if(mode=="daily")
        {
            var pair=Pair(a[0],b[0],lagDays);
            return new{status=pair.Status,reason=pair.Reason,statistics=pair.Summary,limitations=Observational,
                days=pair.Pairs.Select(p=>new{date=p.A.Date,outcomeDate=p.B.Date,exposure=p.A.Value,outcome=p.B.Value,
                    exposureSamples=p.A.Samples,outcomeSamples=p.B.Samples,supporting=Refs(p.A.Evidence.Concat(p.B.Evidence))})};
        }
        return EventContrast(a[0],b[0],windowMinutes,delayMinutes);
    }
    static object EventContrast(HealthSeries a,HealthSeries b,int minutes,int delay)
    {
        if(a.Meaning?.Event!=true)return new{status="cannot_evaluate",reason="Exposure must have registered event semantics; sampled or daily metrics are not event onsets."};
        if(a.Samples.Any(s=>s.Source.At is null)||b.Samples.Any(s=>s.Source.At is null))return new{status="cannot_evaluate",reason="Event windows require source instants. Unverified local clocks are not assigned an offset."};
        var events=a.Samples.Where(s=>s.Exact).OrderBy(s=>s.Source.At).ToArray();var outcomes=b.Samples.Where(s=>s.Exact).OrderBy(s=>s.Source.At).ToArray();
        if(events.Length>500)return new{status="cannot_evaluate",reason="More than 500 events; narrow the requested interval. No partial event analysis performed."};
        var windows=new List<object>();var differences=new List<double>();int excluded=0,controls=0;
        foreach(var e in events)
        {
            var at=e.Source.At!.Value;
            // Avoid overlapping exposures and source daily totals mislabeled as sampled outcomes.
            if(events.Any(other=>other!=e && other.Source.At>=at.AddMinutes(-minutes) && other.Source.At<at.AddMinutes(delay+minutes))){excluded++;continue;}
            var pre=Slice(at.AddMinutes(-minutes),at);var post=Slice(at.AddMinutes(delay),at.AddMinutes(delay+minutes));
            if(!Dense(pre)||!Dense(post)){excluded++;continue;}
            var diff=post.Average(s=>s.Value!.Value)-pre.Average(s=>s.Value!.Value);differences.Add(diff);
            var controlAt=at.AddDays(-7);var controlPre=Slice(controlAt.AddMinutes(-minutes),controlAt);var controlPost=Slice(controlAt.AddMinutes(delay),controlAt.AddMinutes(delay+minutes));
            var control= !events.Any(x=>x.Source.At>=controlAt.AddMinutes(-minutes)&&x.Source.At<controlAt.AddMinutes(delay+minutes)) && Dense(controlPre)&&Dense(controlPost);
            if(control)controls++;
            windows.Add(new{eventTime=at,beforeMean=pre.Average(s=>s.Value!.Value),afterMean=post.Average(s=>s.Value!.Value),difference=diff,
                beforeSamples=pre.Length,afterSamples=post.Length,controlDifference=control?(double?)(controlPost.Average(s=>s.Value!.Value)-controlPre.Average(s=>s.Value!.Value)):null,
                supporting=Refs(new[]{e}.Concat(pre.Take(2)).Concat(post.Take(2))),controlEvidence=control?Refs(controlPre.Take(2).Concat(controlPost.Take(2))):[]});
        }
        return new{status=windows.Count<3?"cannot_evaluate":"evaluated",reason=windows.Count<3?"Fewer than three non-overlapping, adequately sampled event windows.":null,
            exposure=a.Id,outcome=b.Id,windowMinutes=minutes,delayMinutes=delay,eventWindows=windows.Count,excludedEvents=excluded,matchedControls=controls,
            meanChange=differences.Count>0?(double?)differences.Average():null,increased=differences.Count(x=>x>0),decreased=differences.Count(x=>x<0),unchanged=differences.Count(x=>x==0),
            windows=windows.Take(30),truncated=windows.Count>30,limitations=new[]{"Sample means, not time-weighted effects. Requires at least three readings per window spanning half the window with no gap greater than half the window.",
                "Controls use the same UTC clock time seven days earlier when available; daylight-saving/local-time, unrecorded exposures, medication and selection confounding remain.","No control means no controlled comparison; no causal or treatment conclusion."}};
        CleanSample[] Slice(DateTimeOffset start,DateTimeOffset end)=>outcomes.Where(x=>x.Source.At>=start&&x.Source.At<end).ToArray();
        bool Dense(CleanSample[] v)=>v.Length>=3 && (v[^1].Source.At-v[0].Source.At)!.Value.TotalMinutes>=minutes/2.0 &&
            v.Zip(v.Skip(1)).All(p=>(p.Second.Source.At-p.First.Source.At)!.Value.TotalMinutes<=minutes/2.0);
    }
}
