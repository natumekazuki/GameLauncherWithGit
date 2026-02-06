using GameLauncherWithGit.Application.Abstractions;
using GameLauncherWithGit.Domain.Models;
using System.Collections.Concurrent;

namespace GameLauncherWithGit.Application.Services;

public sealed class RepositoryStateStore : IRepositoryStateStore
{
	private readonly ConcurrentDictionary<string, RepositorySyncState> _states = new(StringComparer.OrdinalIgnoreCase);

	public RepositorySyncState GetStateOrDefault(string repositoryId)
	{
		if (string.IsNullOrWhiteSpace(repositoryId))
		{
			return RepositorySyncState.Idle;
		}

		return _states.GetValueOrDefault(repositoryId, RepositorySyncState.Idle);
	}

	public IReadOnlyDictionary<string, RepositorySyncState> Snapshot()
	{
		return _states.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
	}

	public void SetState(string repositoryId, RepositorySyncState state)
	{
		if (string.IsNullOrWhiteSpace(repositoryId))
		{
			return;
		}

		_states[repositoryId] = state;
	}
}
