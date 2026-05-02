using Sandbox.UI;

public sealed class JobVoteManager : Component
{
	public const float VoteDurationSeconds = 60.0f;

	[Property, Sync( SyncFlags.FromHost )]
	public bool IsVoteActive { get; private set; }

	[Property, Sync( SyncFlags.FromHost )]
	public Guid CandidatePlayerId { get; private set; }

	[Property, Sync( SyncFlags.FromHost )]
	public string CandidateName { get; private set; }

	[Property, Sync( SyncFlags.FromHost )]
	public string JobDefinitionPath { get; private set; }

	[Property, Sync( SyncFlags.FromHost )]
	public string JobTitle { get; private set; }

	[Property, Sync( SyncFlags.FromHost )]
	public float VoteEndTime { get; private set; }

	[Property, Sync( SyncFlags.FromHost )]
	public int YesVotes { get; private set; }

	[Property, Sync( SyncFlags.FromHost )]
	public int NoVotes { get; private set; }

	[Property, Sync( SyncFlags.FromHost )]
	public int Revision { get; private set; }

	readonly Dictionary<Guid, bool> Votes = [];

	public static JobVoteManager Current => Game.ActiveScene?.Get<JobVoteManager>();
	public JobDefinition JobDefinition => JobDefinition.Get( JobDefinitionPath );

	public float SecondsRemaining
		=> IsVoteActive ? MathF.Max( 0.0f, VoteEndTime - Time.Now ) : 0.0f;

	public static JobVoteManager Ensure( Scene scene )
	{
		if ( scene is null )
			return null;

		var existing = scene.Get<JobVoteManager>();
		if ( existing.IsValid() )
			return existing;

		var go = new GameObject( true, "Job Vote Manager" );
		var manager = go.AddComponent<JobVoteManager>();
		go.NetworkSpawn( null );
		go.Network.SetOwnerTransfer( OwnerTransfer.Fixed );
		return manager;
	}

	public bool TryStartVote( Player candidate, JobDefinition definition, out string reason )
	{
		reason = null;

		if ( !Networking.IsHost )
			return false;

		if ( candidate is null || definition is null )
		{
			reason = "Invalid vote request.";
			return false;
		}

		if ( IsVoteActive && SecondsRemaining > 0.0f )
		{
			reason = "A job vote is already in progress.";
			return false;
		}

		if ( !JobManager.CanJoin( candidate, definition, out reason ) )
			return false;

		var candidateId = candidate.PlayerData?.PlayerId ?? candidate.Network.Owner?.Id ?? Guid.Empty;
		if ( candidateId == Guid.Empty )
		{
			reason = "Unable to identify the candidate.";
			return false;
		}

		Votes.Clear();
		IsVoteActive = true;
		CandidatePlayerId = candidateId;
		CandidateName = candidate.DisplayName;
		JobDefinitionPath = definition.ResourcePath;
		JobTitle = definition.Title;
		VoteEndTime = Time.Now + VoteDurationSeconds;
		YesVotes = 0;
		NoVotes = 0;
		Revision++;

		Scene.Get<Chat>()?.AddSystemText( $"{CandidateName} wants to become {JobTitle}. Open the context menu with C to unlock your mouse and vote.", "how_to_vote" );
		return true;
	}

	[Rpc.Host]
	public void RequestVote( bool support )
	{
		if ( !IsVoteActive )
			return;

		if ( SecondsRemaining <= 0.0f )
		{
			FinishVote();
			return;
		}

		var voter = Player.FindForConnection( Rpc.Caller );
		var voterId = voter?.PlayerData?.PlayerId ?? Rpc.Caller?.Id ?? Guid.Empty;
		if ( voterId == Guid.Empty )
			return;

		if ( Votes.ContainsKey( voterId ) )
		{
			Notices.SendNotice( Rpc.Caller, "how_to_vote", Color.Yellow, "You already voted.", 2 );
			return;
		}

		Votes[voterId] = support;
		UpdateVoteCounts();
		Revision++;

		Notices.SendNotice( Rpc.Caller, "how_to_vote", support ? Color.Green : Color.Orange, "Vote recorded.", 2 );

		if ( Votes.Count >= GetEligibleVoterCount() )
		{
			FinishVote();
		}
	}

	protected override void OnUpdate()
	{
		if ( !Networking.IsHost || !IsVoteActive )
			return;

		if ( SecondsRemaining <= 0.0f )
		{
			FinishVote();
		}
	}

	void FinishVote()
	{
		if ( !Networking.IsHost || !IsVoteActive )
			return;

		UpdateVoteCounts();

		var candidateId = CandidatePlayerId;
		var candidateName = CandidateName;
		var jobPath = JobDefinitionPath;
		var jobTitle = JobTitle;
		var yesVotes = YesVotes;
		var noVotes = NoVotes;

		ClearVote();

		var passed = yesVotes > noVotes;
		if ( !passed )
		{
			Scene.Get<Chat>()?.AddSystemText( $"{candidateName}'s job vote for {jobTitle} failed ({yesVotes} yes / {noVotes} no).", "how_to_vote" );
			return;
		}

		var candidate = Player.For( candidateId );
		var definition = JobDefinition.Get( jobPath );
		if ( !candidate.IsValid() || definition is null )
		{
			Scene.Get<Chat>()?.AddSystemText( $"{candidateName}'s vote passed, but the candidate is no longer available.", "how_to_vote" );
			return;
		}

		if ( !JobManager.CanJoin( candidate, definition, out var reason ) )
		{
			Notices.SendNotice( candidate.Network.Owner, "block", Color.Red, reason, 3 );
			Scene.Get<Chat>()?.AddSystemText( $"{candidateName}'s vote passed, but {jobTitle} is no longer available.", "how_to_vote" );
			return;
		}

		candidate.ApplyVotedJobDefinition( definition );
		Scene.Get<Chat>()?.AddSystemText( $"{candidateName}'s job vote for {jobTitle} passed ({yesVotes} yes / {noVotes} no).", "how_to_vote" );
	}

	void ClearVote()
	{
		Votes.Clear();
		IsVoteActive = false;
		CandidatePlayerId = Guid.Empty;
		CandidateName = null;
		JobDefinitionPath = null;
		JobTitle = null;
		VoteEndTime = 0.0f;
		YesVotes = 0;
		NoVotes = 0;
		Revision++;
	}

	void UpdateVoteCounts()
	{
		YesVotes = Votes.Count( x => x.Value );
		NoVotes = Votes.Count( x => !x.Value );
	}

	int GetEligibleVoterCount()
	{
		return Math.Max( 1, Connection.All.Count( x => x is not null ) );
	}
}
