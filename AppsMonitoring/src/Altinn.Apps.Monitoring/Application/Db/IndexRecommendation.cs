namespace Altinn.Apps.Monitoring.Application.Db;

internal sealed record IndexRecommendation(
    string TableName,
    long TooMuchSeq,
    string Result,
    long TableRelSize,
    long SeqScan,
    long IndexScan
);
