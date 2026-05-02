using Sandbox.UI;

public sealed partial class Player
{
	public const string ThiefJobDefinitionPath = "jobs/thief.jobdef";
	public const string HoboJobDefinitionPath = "jobs/hobo.jobdef";
	const float JobChangeCooldownSeconds = 60.0f;

	const string PrisonerJumpsuitClothingPath = "models/citizen_clothes/shirt/jumpsuit/prison_jumpsuit.clothing";
	const string PrisonerShoesClothingPath = "models/citizen_clothes/shoes/boots/black_boots.clothing";

	TimeSince _timeSinceJobChange = JobChangeCooldownSeconds;

	sealed class BodyAppearanceSnapshot
	{
		public Model Model { get; init; }
		public ulong BodyGroups { get; init; }
		public string MaterialGroup { get; init; }
		public Material MaterialOverride { get; init; }
		public Dictionary<string, float> Morphs { get; init; }
	}

	[Property, Sync( SyncFlags.FromHost )]
	public string JobDefinitionPath { get; private set; } = JobDefinition.DefaultResourcePath;

	public JobDefinition CurrentJobDefinition => JobDefinition.Get( JobDefinitionPath ) ?? JobDefinition.GetDefault();
	public bool IsThief => string.Equals( JobDefinitionPath, ThiefJobDefinitionPath, StringComparison.OrdinalIgnoreCase );

	public void SetJobDefinition( JobDefinition definition )
	{
		if ( !Networking.IsHost || definition is null )
			return;

		CleanupPreviousJobItems( JobDefinitionPath, definition.ResourcePath );

		JobDefinitionPath = definition.ResourcePath;
		SetJobTitle( definition.Title );
		PlayerData?.SetJob( definition.ResourcePath, definition.Title );
		SaveRoleplayData();
	}

	void CleanupPreviousJobItems( string oldJobDefinitionPath, string newJobDefinitionPath )
	{
		if ( string.Equals( oldJobDefinitionPath, newJobDefinitionPath, StringComparison.OrdinalIgnoreCase ) )
			return;

		if ( Network.Owner is not { } owner )
			return;

		if ( string.Equals( oldJobDefinitionPath, MayorJobDefinitionPath, StringComparison.OrdinalIgnoreCase )
			&& !string.Equals( newJobDefinitionPath, MayorJobDefinitionPath, StringComparison.OrdinalIgnoreCase ) )
		{
			Lawboard.DestroyOwned( owner );
			CityLawManager.Current?.ClearLaws();
		}

		if ( string.Equals( oldJobDefinitionPath, HoboJobDefinitionPath, StringComparison.OrdinalIgnoreCase )
			&& !string.Equals( newJobDefinitionPath, HoboJobDefinitionPath, StringComparison.OrdinalIgnoreCase ) )
		{
			TipJar.DestroyOwned( owner );
		}
	}

	public void EnsureValidJobDefinition()
	{
		if ( !Networking.IsHost )
			return;

		var definition = JobDefinition.Get( PlayerData?.JobDefinitionPath )
			?? CurrentJobDefinition
			?? JobDefinition.GetDefault();

		if ( definition is null )
			return;

		SetJobDefinition( definition );
	}

	[Rpc.Host]
	public void RequestSetJob( string resourcePath )
	{
		if ( Rpc.Caller != Network.Owner )
			return;

		var definition = JobDefinition.Get( resourcePath );
		if ( definition is null )
			return;

		if ( !JobManager.CanJoin( this, definition, out var reason ) )
		{
			Notices.SendNotice( Network.Owner, "block", Color.Red, reason, 3 );
			return;
		}

		if ( string.Equals( JobDefinitionPath, definition.ResourcePath, StringComparison.OrdinalIgnoreCase ) )
		{
			Notices.SendNotice( Network.Owner, "person", Color.Yellow, $"You are already {definition.Title}.", 3 );
			return;
		}

		if ( _timeSinceJobChange < JobChangeCooldownSeconds )
		{
			var remaining = MathF.Ceiling( JobChangeCooldownSeconds - _timeSinceJobChange );
			Notices.SendNotice( Network.Owner, "schedule", Color.Orange, $"Wait {FormatJobChangeCooldown( remaining )} before changing jobs again.", 3 );
			return;
		}

		if ( definition.RequiresVote )
		{
			var manager = JobVoteManager.Current ?? JobVoteManager.Ensure( Scene );
			string voteReason = null;
			if ( manager is null || !manager.TryStartVote( this, definition, out voteReason ) )
			{
				Notices.SendNotice( Network.Owner, "block", Color.Red, voteReason ?? "Unable to start job vote.", 3 );
				return;
			}

			_timeSinceJobChange = 0;
			Notices.SendNotice( Network.Owner, "how_to_vote", Color.Cyan, $"Vote started for {definition.Title}.", 3 );
			return;
		}

		SetJobDefinition( definition );
		_timeSinceJobChange = 0;
		_ = ApplyJobDefinitionAsync( definition, true );
	}

	public void ApplyVotedJobDefinition( JobDefinition definition )
	{
		if ( !Networking.IsHost || definition is null )
			return;

		_timeSinceJobChange = 0;
		_ = ApplyJobDefinitionAsync( definition, true );
	}

	static string FormatJobChangeCooldown( float seconds )
	{
		var wholeSeconds = Math.Max( 0, (int)MathF.Ceiling( seconds ) );
		var minutes = wholeSeconds / 60;
		var remainingSeconds = wholeSeconds % 60;

		if ( minutes <= 0 )
			return $"{remainingSeconds}s";

		if ( remainingSeconds <= 0 )
			return $"{minutes}m";

		return $"{minutes}m {remainingSeconds}s";
	}

	public Task ApplyCurrentJobAfterSpawnAsync()
	{
		var definition = CurrentJobDefinition;
		return definition is null ? Task.CompletedTask : ApplyJobDefinitionAsync( definition, false );
	}

	async Task ApplyJobDefinitionAsync( JobDefinition definition, bool notifyPlayer )
	{
		if ( !Networking.IsHost || definition is null )
			return;

		SetJobDefinition( definition );

		var loadout = GetComponent<PlayerLoadout>();
		if ( loadout.IsValid() )
		{
			await loadout.ApplyJobLoadoutAsync( definition.StartingItems ?? [] );
		}

		await ApplyJobClothingAsync( definition );

		if ( notifyPlayer )
		{
			Notices.SendNotice( Network.Owner, "badge", Color.Green, $"You are now {definition.Title}.", 3 );
		}
	}

	async Task ApplyJobClothingAsync( JobDefinition definition )
	{
		if ( definition?.UseOwnerAvatarAppearance == true )
		{
			await ApplyOwnerAvatarAppearanceAsync();
			return;
		}

		await ApplyClothingAsync( definition?.Clothing, definition?.PreserveOwnerAvatarAppearance == true );
	}

	async Task ApplyPrisonerClothingAsync()
	{
		await ApplyClothingAsync( [PrisonerJumpsuitClothingPath, PrisonerShoesClothingPath], false );
	}

	async Task RestoreJobClothingAsync()
	{
		await ApplyJobClothingAsync( CurrentJobDefinition );
	}

	async Task ApplyOwnerAvatarAppearanceAsync()
	{
		if ( !Body.IsValid() )
			return;

		var dresser = Body.GetComponentInChildren<Dresser>( true );
		if ( !dresser.IsValid() )
			return;

		dresser.Clothing.Clear();
		dresser.Clear();
		dresser.Source = Dresser.ClothingSource.OwnerConnection;
		await dresser.Apply();
		Body.Network?.Refresh();
		GameObject.Network?.Refresh();
	}

	async Task ApplyClothingAsync( IEnumerable<string> clothingPaths, bool preserveOwnerAvatarAppearance )
	{
		if ( !Body.IsValid() )
			return;

		var dresser = Body.GetComponentInChildren<Dresser>( true );
		if ( !dresser.IsValid() )
			return;

		var bodyRenderer = dresser.BodyTarget.IsValid()
			? dresser.BodyTarget
			: Body.GetComponent<SkinnedModelRenderer>( true );

		var appearance = CaptureBodyAppearance( bodyRenderer );
		var jobClothing = ResolveClothing( clothingPaths ).ToArray();
		var ownerAppearance = preserveOwnerAvatarAppearance && Network.Owner is not null
			? ClothingContainer.CreateFromConnection( Network.Owner, dresser.RemoveUnownedItems )
			: null;

		dresser.Clothing.Clear();

		foreach ( var clothing in jobClothing )
		{
			AddClothing( dresser.Clothing, clothing );
		}

		if ( ownerAppearance is not null )
		{
			foreach ( var entry in ownerAppearance.Clothing )
			{
				if ( !CanPreserveOwnerClothing( entry.Clothing, jobClothing ) )
					continue;

				dresser.Clothing.Add( entry );
			}
		}

		foreach ( var clothing in jobClothing )
		{
			if ( !HasClothing( dresser.Clothing, clothing ) )
				AddClothing( dresser.Clothing, clothing );
		}

		dresser.Clear();
		dresser.Source = Dresser.ClothingSource.Manual;
		await dresser.Apply();
		RestoreBodyAppearance( bodyRenderer, appearance );
		Body.Network?.Refresh();
		GameObject.Network?.Refresh();
	}

	static IEnumerable<Clothing> ResolveClothing( IEnumerable<string> clothingPaths )
	{
		foreach ( var clothingPath in clothingPaths ?? [] )
		{
			if ( string.IsNullOrWhiteSpace( clothingPath ) )
				continue;

			var clothing = ResourceLibrary.Get<Clothing>( clothingPath );
			if ( clothing is null )
				continue;

			yield return clothing;
		}
	}

	static void AddClothing( ICollection<ClothingContainer.ClothingEntry> clothingEntries, Clothing clothing )
	{
		if ( clothingEntries is null || clothing is null )
			return;

		clothingEntries.Add( new ClothingContainer.ClothingEntry
		{
			Clothing = clothing
		} );
	}

	static bool HasClothing( IEnumerable<ClothingContainer.ClothingEntry> entries, Clothing clothing )
	{
		if ( entries is null || clothing is null )
			return false;

		return entries.Any( entry => entry.Clothing == clothing
			|| string.Equals( entry.Clothing?.ResourcePath, clothing.ResourcePath, StringComparison.OrdinalIgnoreCase ) );
	}

	static bool CanPreserveOwnerClothing( Clothing ownerClothing, IReadOnlyList<Clothing> jobClothing )
	{
		if ( ownerClothing is null )
			return true;

		return jobClothing.All( job => job is null || (ownerClothing.CanBeWornWith( job ) && job.CanBeWornWith( ownerClothing )) );
	}

	static BodyAppearanceSnapshot CaptureBodyAppearance( SkinnedModelRenderer renderer )
	{
		if ( !renderer.IsValid() )
			return null;

		Dictionary<string, float> morphs = null;
		if ( renderer.SceneModel is not null )
		{
			morphs = renderer.Morphs.Names.ToDictionary( name => name, name => renderer.SceneModel.Morphs.Get( name ) );
		}

		return new BodyAppearanceSnapshot
		{
			Model = renderer.Model,
			BodyGroups = renderer.BodyGroups,
			MaterialGroup = renderer.MaterialGroup,
			MaterialOverride = renderer.MaterialOverride,
			Morphs = morphs
		};
	}

	static void RestoreBodyAppearance( SkinnedModelRenderer renderer, BodyAppearanceSnapshot appearance )
	{
		if ( !renderer.IsValid() || appearance is null )
			return;

		renderer.Model = appearance.Model;
		renderer.BodyGroups = appearance.BodyGroups;
		renderer.MaterialGroup = appearance.MaterialGroup;
		renderer.MaterialOverride = appearance.MaterialOverride;

		if ( appearance.Morphs is null || renderer.SceneModel is null )
			return;

		foreach ( var name in renderer.Morphs.Names )
		{
			renderer.SceneModel.Morphs.Reset( name );
		}

		foreach ( var (name, value) in appearance.Morphs )
		{
			renderer.SceneModel.Morphs.Set( name, value );
		}
	}
}
