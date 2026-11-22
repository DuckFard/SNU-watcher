namespace SnuWatcher.Infrastructure.Persistence;

public sealed partial class SqliteWatchStore
{
    // Additive V3 producer DDL. Keep prior producer definitions unchanged for exact recognition.
    private const string BatchSchemaSql = """
        CREATE TABLE DeliveryBatches(id TEXT PRIMARY KEY,sectionKey TEXT NOT NULL REFERENCES SectionIdentities(sectionKey),revision INTEGER NOT NULL CHECK(revision>0),status INTEGER NOT NULL CHECK(status IN(0,1)),foldCutoff INTEGER NOT NULL CHECK(foldCutoff>=0),foldCursor INTEGER NOT NULL CHECK(foldCursor>=0),foldComplete INTEGER NOT NULL CHECK(foldComplete IN(0,1)),foldAfterAttempt INTEGER NOT NULL DEFAULT 0 CHECK(foldAfterAttempt>=0),createdUtc TEXT NOT NULL);
        CREATE UNIQUE INDEX OpenDeliveryBatch ON DeliveryBatches(sectionKey) WHERE status=0;
        CREATE TABLE DeliveryBatchEvents(batchId TEXT NOT NULL REFERENCES DeliveryBatches(id),eventId TEXT NOT NULL UNIQUE REFERENCES Events(id),claimedRevision INTEGER NOT NULL CHECK(claimedRevision>0),joinedRevision INTEGER NOT NULL CHECK(joinedRevision>0),PRIMARY KEY(batchId,eventId));
        CREATE TABLE DeliveryBatchAttempts(id TEXT PRIMARY KEY,batchId TEXT NOT NULL REFERENCES DeliveryBatches(id),reservationId TEXT NOT NULL UNIQUE,sequence INTEGER NOT NULL CHECK(sequence>0),snapshot TEXT NOT NULL,preparedFacts TEXT NOT NULL,status INTEGER NOT NULL CHECK(status IN(0,1)),UNIQUE(batchId,sequence));
        CREATE UNIQUE INDEX UnresolvedBatchAttempt ON DeliveryBatchAttempts(batchId) WHERE status=0;
        CREATE TABLE DeliveryBatchAttemptEvents(attemptId TEXT NOT NULL REFERENCES DeliveryBatchAttempts(id),eventId TEXT NOT NULL REFERENCES Events(id),triggerOrdinal INTEGER NOT NULL CHECK(triggerOrdinal>0),expectedRevision INTEGER NOT NULL CHECK(expectedRevision>0),attemptsBefore INTEGER NOT NULL CHECK(attemptsBefore>=0),PRIMARY KEY(attemptId,eventId));
        CREATE INDEX BatchAttemptCoverage ON DeliveryBatchAttemptEvents(attemptId,triggerOrdinal);
        CREATE TABLE DeliveryBatchResults(attemptId TEXT PRIMARY KEY REFERENCES DeliveryBatchAttempts(id),sourceFacts TEXT NOT NULL,receipt TEXT NOT NULL);
        CREATE TABLE SourcePolicies(sourceId TEXT PRIMARY KEY,revision INTEGER NOT NULL CHECK(revision>=0),issuedOrdinal INTEGER NOT NULL CHECK(issuedOrdinal>=0),snapshot TEXT NOT NULL);
        CREATE TABLE SourceRequests(id TEXT PRIMARY KEY,sourceId TEXT NOT NULL REFERENCES SourcePolicies(sourceId),ordinal INTEGER NOT NULL CHECK(ordinal>0),kind INTEGER NOT NULL,issuedUtc TEXT NOT NULL,resumeWatermark INTEGER NOT NULL CHECK(resumeWatermark>=0),UNIQUE(sourceId,ordinal));
        CREATE TABLE SourceRequestResults(requestId TEXT PRIMARY KEY REFERENCES SourceRequests(id),sourceFacts TEXT NOT NULL,receipt TEXT NOT NULL);
        """;
}
