// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Snapshots;

/// <summary>
/// Manages saving, loading, and querying snapshots from persistent storage.
/// </summary>
public interface ISnapshotStore
{
    /// <summary>
    /// Gets the file path for a snapshot.
    /// </summary>
    /// <param name="testName">The test name identifying the snapshot.</param>
    /// <param name="suffix">Optional suffix for snapshot variants.</param>
    /// <returns>The full file path to the snapshot.</returns>
    string GetSnapshotPath(string testName, string suffix = "");

    /// <summary>
    /// Saves a snapshot asynchronously.
    /// </summary>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="testName">The test name identifying the snapshot.</param>
    /// <param name="value">The value to save.</param>
    /// <param name="suffix">Optional suffix for snapshot variants.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SaveAsync<T>(string testName, T value, string suffix = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a snapshot if it exists. Returns <c>default</c> if the snapshot file is not found.
    /// </summary>
    /// <typeparam name="T">The type to deserialize the snapshot to.</typeparam>
    /// <param name="testName">The test name identifying the snapshot.</param>
    /// <param name="suffix">Optional suffix for snapshot variants.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deserialized snapshot, or <c>default</c> if not found.</returns>
    Task<T?> LoadAsync<T>(string testName, string suffix = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if a snapshot exists.
    /// </summary>
    /// <param name="testName">The test name identifying the snapshot.</param>
    /// <param name="suffix">Optional suffix for snapshot variants.</param>
    /// <returns><c>true</c> if the snapshot file exists.</returns>
    bool Exists(string testName, string suffix = "");

    /// <summary>
    /// Deletes a snapshot if it exists.
    /// </summary>
    /// <param name="testName">The test name identifying the snapshot.</param>
    /// <param name="suffix">Optional suffix for snapshot variants.</param>
    /// <returns><c>true</c> if the file was deleted; <c>false</c> if it did not exist.</returns>
    bool Delete(string testName, string suffix = "");

    /// <summary>
    /// Lists all snapshot names in the store.
    /// </summary>
    /// <returns>A read-only list of snapshot file names (without extension).</returns>
    IReadOnlyList<string> ListSnapshots();

    /// <summary>
    /// Gets the number of snapshots in the store.
    /// </summary>
    int Count { get; }
}
