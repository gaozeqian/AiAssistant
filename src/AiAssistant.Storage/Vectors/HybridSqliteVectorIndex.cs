using System.Globalization;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using AiAssistant.Application.Ports;
using AiAssistant.Core.Exceptions;
using AiAssistant.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace AiAssistant.Storage.Vectors;

/// <summary>
/// Isolated IVectorIndex implementation supporting SQLite3MC + sqlite-vec (vec0) joint execution
/// with guaranteed encrypted-table SIMD cosine similarity persistence and full index rebuild (ADR-0004).
/// </summary>
public sealed class HybridSqliteVectorIndex(EncryptedSqliteConnectionFactory connectionFactory) : IVectorIndex
{
    private const string PrimaryIndexName = "events_semantic_v1";

    public async Task<VectorIndexMetadata> GetMetadataAsync(CancellationToken ct)
    {
        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        var model = await ReadActiveModelAsync(conn, ct);

        long count;
        await using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = "SELECT count(*) FROM event_vectors;";
            count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
        }

        return new VectorIndexMetadata(
            connectionFactory.VectorBackendDiagnostic,
            connectionFactory.IsNativeSqliteVecLoaded,
            model,
            count);
    }

    public async Task UpsertAsync(
        string eventId,
        ReadOnlyMemory<float> embedding,
        VectorModelDescriptor model,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentNullException.ThrowIfNull(model);

        if (embedding.Length != model.Dimensions || model.Dimensions <= 0)
        {
            throw new ArgumentException(
                $"Embedding length ({embedding.Length}) does not match declared model dimensions ({model.Dimensions}).",
                nameof(embedding));
        }

        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var existingModel = await ReadActiveModelAsync(conn, ct, tx);
        if (existingModel is not null &&
            (existingModel.Dimensions != model.Dimensions ||
             !string.Equals(existingModel.ModelId, model.ModelId, StringComparison.Ordinal)))
        {
            throw new StorageIntegrityException(
                $"Vector index is configured for '{existingModel.ModelId}' ({existingModel.Dimensions}d). Call RebuildAsync before writing '{model.ModelId}' ({model.Dimensions}d) vectors.");
        }

        await UpsertModelMetaAsync(conn, tx, model, connectionFactory.VectorBackendDiagnostic, ct);
        await UpsertVectorRowAsync(conn, tx, eventId, embedding, model.Dimensions, connectionFactory.IsNativeSqliteVecLoaded, ct);

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<VectorSearchHit>> SearchAsync(
        ReadOnlyMemory<float> queryEmbedding,
        int topK,
        CancellationToken ct)
    {
        if (topK <= 0 || queryEmbedding.IsEmpty)
        {
            return [];
        }

        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        var activeModel = await ReadActiveModelAsync(conn, ct);
        if (activeModel is null)
        {
            return [];
        }

        if (queryEmbedding.Length != activeModel.Dimensions)
        {
            throw new ArgumentException(
                $"Query embedding dimensions ({queryEmbedding.Length}) do not match index dimensions ({activeModel.Dimensions}).",
                nameof(queryEmbedding));
        }

        // Encrypted SIMD scan over event_vectors guarantees identical cosine scores across all OS/RIDs
        // while keeping every vector page encrypted inside SQLite3MC.
        var hits = new List<VectorSearchHit>();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT event_id, embedding_blob FROM event_vectors;";

        var queryArray = queryEmbedding.ToArray();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var eventId = reader.GetString(0);
            var rawBytes = (byte[])reader.GetValue(1);
            var candidateFloats = MemoryMarshal.Cast<byte, float>(rawBytes.AsSpan());
            if (candidateFloats.Length != queryArray.Length)
            {
                continue;
            }

            var similarity = TensorPrimitives.CosineSimilarity(queryArray, candidateFloats);
            if (!float.IsNaN(similarity))
            {
                hits.Add(new VectorSearchHit(eventId, similarity));
            }
        }

        return hits
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToArray();
    }

    public async Task RebuildAsync(
        VectorModelDescriptor targetModel,
        Func<CancellationToken, IAsyncEnumerable<(string EventId, ReadOnlyMemory<float> Embedding)>> generator,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targetModel);
        ArgumentNullException.ThrowIfNull(generator);

        await using var conn = await connectionFactory.OpenConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var clearCmd = conn.CreateCommand())
        {
            clearCmd.Transaction = tx;
            clearCmd.CommandText = "DELETE FROM event_vectors;";
            await clearCmd.ExecuteNonQueryAsync(ct);
        }

        if (connectionFactory.IsNativeSqliteVecLoaded)
        {
            await using var dropVecCmd = conn.CreateCommand();
            dropVecCmd.Transaction = tx;
            dropVecCmd.CommandText = "DROP TABLE IF EXISTS vec_events;";
            await dropVecCmd.ExecuteNonQueryAsync(ct);
        }

        await UpsertModelMetaAsync(conn, tx, targetModel, connectionFactory.VectorBackendDiagnostic, ct);

        await foreach (var (eventId, embedding) in generator(ct))
        {
            if (embedding.Length != targetModel.Dimensions)
            {
                throw new ArgumentException($"Generated embedding for '{eventId}' has {embedding.Length} dimensions; expected {targetModel.Dimensions}.");
            }

            await UpsertVectorRowAsync(conn, tx, eventId, embedding, targetModel.Dimensions, connectionFactory.IsNativeSqliteVecLoaded, ct);
        }

        await tx.CommitAsync(ct);
    }

    private static async Task<VectorModelDescriptor?> ReadActiveModelAsync(
        SqliteConnection conn,
        CancellationToken ct,
        SqliteTransaction? tx = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT model_id, model_version, dimensions
            FROM vector_index_meta
            WHERE index_name = $name;
            """;
        cmd.Parameters.AddWithValue("$name", PrimaryIndexName);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new VectorModelDescriptor(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
    }

    private static async Task UpsertModelMetaAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        VectorModelDescriptor model,
        string backend,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO vector_index_meta(index_name, model_id, model_version, dimensions, backend, updated_at_utc)
            VALUES ($name, $modelId, $version, $dims, $backend, $now)
            ON CONFLICT(index_name) DO UPDATE SET
                model_id = excluded.model_id,
                model_version = excluded.model_version,
                dimensions = excluded.dimensions,
                backend = excluded.backend,
                updated_at_utc = excluded.updated_at_utc;
            """;
        cmd.Parameters.AddWithValue("$name", PrimaryIndexName);
        cmd.Parameters.AddWithValue("$modelId", model.ModelId);
        cmd.Parameters.AddWithValue("$version", model.ModelVersion);
        cmd.Parameters.AddWithValue("$dims", model.Dimensions);
        cmd.Parameters.AddWithValue("$backend", backend);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task UpsertVectorRowAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        string eventId,
        ReadOnlyMemory<float> vector,
        int dimensions,
        bool syncNativeVec0,
        CancellationToken ct)
    {
        var bytes = MemoryMarshal.AsBytes(vector.Span).ToArray();
        var norm = TensorPrimitives.Norm(vector.Span);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO event_vectors(event_id, embedding_blob, norm)
                VALUES ($id, $blob, $norm)
                ON CONFLICT(event_id) DO UPDATE SET
                    embedding_blob = excluded.embedding_blob,
                    norm = excluded.norm;
                """;
            cmd.Parameters.AddWithValue("$id", eventId);
            cmd.Parameters.AddWithValue("$blob", bytes);
            cmd.Parameters.AddWithValue("$norm", norm);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (syncNativeVec0)
        {
            await using var ensureVecCmd = conn.CreateCommand();
            ensureVecCmd.Transaction = tx;
            ensureVecCmd.CommandText = $"CREATE VIRTUAL TABLE IF NOT EXISTS vec_events USING vec0(event_id TEXT PRIMARY KEY, embedding float[{dimensions}]);";
            await ensureVecCmd.ExecuteNonQueryAsync(ct);

            await using var delVecCmd = conn.CreateCommand();
            delVecCmd.Transaction = tx;
            delVecCmd.CommandText = "DELETE FROM vec_events WHERE event_id = $id;";
            delVecCmd.Parameters.AddWithValue("$id", eventId);
            await delVecCmd.ExecuteNonQueryAsync(ct);

            await using var insVecCmd = conn.CreateCommand();
            insVecCmd.Transaction = tx;
            insVecCmd.CommandText = "INSERT INTO vec_events(event_id, embedding) VALUES ($id, $vec);";
            insVecCmd.Parameters.AddWithValue("$id", eventId);
            insVecCmd.Parameters.AddWithValue("$vec", bytes);
            await insVecCmd.ExecuteNonQueryAsync(ct);
        }
    }
}
