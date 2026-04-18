using Sandbox.UI;

public sealed class MoneyPrinter : Component, Component.IPressable, IPhysgunEvent, Component.IDamageable
{
	const string BaseMaterialPath = "materials/printer/base/base.vmat";
	const string FanMaterialPath = "materials/printer/fan/fan.vmat";
	const string DestroyEffectPrefabPath = "entities/particles/fx_sparks.prefab";
	static readonly Dictionary<string, BBox> CachedBounds = new();

	List<TextRenderer> Labels = new();
	ModelRenderer BaseRenderer = default;
	ModelRenderer FanRenderer = default;
	GameObject FanObject = default;
	TimeSince _timeSincePrinted;
	bool _destroyedByDamage;

	[Property, Group( "Damage" ), Range( 1, 500 ), Sync( SyncFlags.FromHost )]
	public float Health { get; set; } = 100.0f;

	[Property, Group( "Damage" ), Range( 1, 500 )]
	public float MaxHealth { get; set; } = 100.0f;

	[Property, Sync( SyncFlags.FromHost ), Change( nameof( OnDefinitionPathChanged ) )]
	public string DefinitionPath { get; set; } = MoneyPrinterDefinition.DefaultResourcePath;

	[Property, Sync( SyncFlags.FromHost ), Change( nameof( OnStoredMoneyChanged ) )]
	public int StoredMoney { get; set; }

	IPressable.Tooltip? IPressable.GetTooltip( IPressable.Event e )
	{
		if ( GetDefinition() is not { } definition )
			return null;

		var description = StoredMoney > 0
			? $"{definition.Title} - ${StoredMoney:n0} - {GetHealthPercent():0}%"
			: $"{definition.Title} - Empty - {GetHealthPercent():0}%";

		return new IPressable.Tooltip( "Collect", "$", description );
	}

	bool IPressable.CanPress( IPressable.Event e ) => StoredMoney > 0;

	void IPhysgunEvent.OnPhysgunGrab( IPhysgunEvent.GrabEvent e )
	{
		if ( !e.Pulling )
		{
			e.Cancelled = true;
		}
	}

	bool IPressable.Press( IPressable.Event e )
	{
		CollectMoney( e.Source.GameObject );
		return true;
	}

	protected override void OnStart()
	{
		CacheComponents();
		ApplyDefinition();
		RefreshLabels();
		_timeSincePrinted = 0;
	}

	protected override void OnUpdate()
	{
		if ( !FanObject.IsValid() || GetDefinition() is null )
			return;

		FanObject.LocalRotation *= Rotation.FromAxis( Vector3.Left, -900.0f * Time.Delta );
	}

	protected override void OnFixedUpdate()
	{
		if ( !Networking.IsHost || GetDefinition() is not { } definition )
			return;

		if ( StoredMoney >= definition.MaxStoredMoney || _timeSincePrinted < definition.Interval )
			return;

		_timeSincePrinted = 0;
		StoredMoney = Math.Min( definition.MaxStoredMoney, StoredMoney + definition.MoneyPerTick );
	}

	void OnDefinitionPathChanged( string oldPath, string newPath )
	{
		ApplyDefinition();
		RefreshLabels();
	}

	void OnStoredMoneyChanged( int oldAmount, int newAmount )
	{
		RefreshLabels();
	}

	[Rpc.Host]
	void CollectMoney( GameObject presserObject )
	{
		if ( StoredMoney <= 0 || !presserObject.IsValid() )
			return;

		var player = presserObject.Root.GetComponent<Player>();
		if ( !player.IsValid() )
			return;

		var collected = StoredMoney;
		StoredMoney = 0;
		player.GiveMoney( collected );

		if ( player.Network.Owner is { } owner )
		{
			Notices.SendNotice( owner, "$", Color.Green, $"Collected ${collected:n0} from the printer.", 3 );
		}

		PlayCollectEffects();
	}

	void IDamageable.OnDamage( in DamageInfo damage )
	{
		if ( IsProxy || _destroyedByDamage || GameObject.IsDestroyed )
			return;

		if ( damage.Damage <= 0.0f )
			return;

		Health = MathF.Max( 0.0f, Health - damage.Damage );
		if ( Health > 0.0f )
			return;

		DestroyFromDamage( damage );
	}

	void DestroyFromDamage( in DamageInfo damage )
	{
		if ( _destroyedByDamage )
			return;

		_destroyedByDamage = true;
		var ownerConnection = GameObject.GetComponent<Ownable>()?.Owner;
		var attacker = damage.Attacker?.Root.GetComponent<Player>();

		if ( ownerConnection is not null )
		{
			var attackerName = attacker.IsValid() ? attacker.DisplayName : "Someone";
			Notices.SendNotice( ownerConnection, "warning", Color.Orange, $"{attackerName} destroyed your printer.", 3 );
		}

		if ( attacker.IsValid() && attacker.Network.Owner != ownerConnection )
		{
			Notices.SendNotice( attacker.Network.Owner, "warning", Color.Green, "Printer destroyed.", 2 );
		}

		PlayDestroyEffects( WorldPosition );
		GameObject.Destroy();
	}

	[Rpc.Broadcast]
	void PlayCollectEffects()
	{
		if ( Application.IsDedicatedServer )
			return;

		Sound.Play( "sounds/ui/ui.spawn.sound", WorldPosition );
	}

	[Rpc.Broadcast]
	static async void PlayDestroyEffects( Vector3 position )
	{
		if ( Application.IsDedicatedServer )
			return;

		Sound.Play( "sounds/ui/ui.undo.sound", position );

		var effectPrefab = GameObject.GetPrefab( DestroyEffectPrefabPath );
		if ( effectPrefab is null )
			return;

		var effect = effectPrefab.Clone( new CloneConfig
		{
			Transform = new Transform( position, Rotation.Identity ),
			StartEnabled = true
		} );

		effect.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		await global::System.Threading.Tasks.Task.Delay( 2000 );
		effect?.Destroy();
	}

	void CacheComponents()
	{
		BaseRenderer = GameObject.GetComponent<ModelRenderer>();
		FanObject = GameObject.Children.FirstOrDefault( x => x.Name == "fan" );
		FanRenderer = FanObject?.GetComponent<ModelRenderer>();
		Labels = EnumerateSelfAndDescendants( GameObject )
			.SelectMany( x => x.Components.GetAll<TextRenderer>() )
			.Where( x => x.IsValid() )
			.ToList();
	}

	void ApplyDefinition()
	{
		if ( GetDefinition() is not { } definition )
			return;

		if ( BaseRenderer.IsValid() )
		{
			BaseRenderer.MaterialOverride = Material.Load( BaseMaterialPath );
			BaseRenderer.Tint = definition.Tint;
		}

		if ( FanRenderer.IsValid() )
		{
			FanRenderer.MaterialOverride = Material.Load( FanMaterialPath );
			FanRenderer.Tint = definition.Tint;
		}
	}

	MoneyPrinterDefinition GetDefinition()
	{
		return MoneyPrinterDefinition.Get( DefinitionPath );
	}

	float GetHealthPercent()
	{
		if ( MaxHealth <= 0.0f )
			return 0.0f;

		return (Health / MaxHealth * 100.0f).Clamp( 0.0f, 100.0f );
	}

	void RefreshLabels()
	{
		if ( Labels.Count == 0 )
		{
			CacheComponents();
		}

		var labelText = $"${MoneyFormatter.FormatCompact( StoredMoney )}";
		foreach ( var label in Labels )
		{
			if ( !label.IsValid() )
				continue;

			var textScope = label.TextScope;
			if ( textScope.Text == labelText )
				continue;

			textScope.Text = labelText;
			label.TextScope = textScope;
		}
	}

	public static int CountOwned( Connection owner )
	{
		if ( owner is null || Game.ActiveScene is null )
			return 0;

		return Game.ActiveScene.GetAllComponents<MoneyPrinter>()
			.Count( x => x.IsValid() && x.GameObject.GetComponent<Ownable>()?.Owner == owner );
	}

	public static bool TrySpawn( Player owner, MoneyPrinterDefinition definition )
	{
		if ( !Networking.IsHost || !owner.IsValid() || definition is null || definition.Prefab is null )
			return false;

		var bounds = GetSpawnBounds( definition.Prefab );

		var eyes = owner.EyeTransform;
		var trace = Game.SceneTrace.Ray( eyes.Position, eyes.Position + eyes.Forward * 200.0f )
			.IgnoreGameObject( owner.GameObject )
			.WithoutTags( "player" )
			.Run();

		var up = trace.Normal.Length.AlmostEqual( 0.0f ) ? Vector3.Up : trace.Normal;
		var backward = -eyes.Forward;
		var right = Vector3.Cross( up, backward ).Normal;
		var forward = Vector3.Cross( right, up ).Normal;

		if ( forward.Length.AlmostEqual( 0.0f ) )
			forward = owner.WorldRotation.Forward.WithZ( 0 ).Normal;

		var spawnTransform = new Transform( trace.EndPosition, Rotation.LookAt( forward, up ) );
		spawnTransform.Position += spawnTransform.Up * -bounds.Mins.z;
		spawnTransform.Rotation *= Rotation.FromYaw( GetYawCorrection( bounds ) );

		var dropped = GameObject.Clone( definition.Prefab, new CloneConfig
		{
			Transform = spawnTransform,
			StartEnabled = false
		} );

		var printer = dropped.GetComponent<MoneyPrinter>( true );
		if ( !printer.IsValid() )
		{
			dropped.Destroy();
			return false;
		}

		printer.DefinitionPath = definition.ResourcePath;
		printer.StoredMoney = 0;
		printer.Health = printer.MaxHealth;
		printer.CacheComponents();
		printer.ApplyDefinition();
		printer.RefreshLabels();

		dropped.Tags.Add( "removable" );
		Ownable.Set( dropped, owner.Network.Owner );
		dropped.NetworkSpawn();

		if ( dropped.GetComponent<Rigidbody>() is { } rb )
		{
			rb.Velocity = owner.Controller.Velocity;
		}

		return true;
	}

	static BBox GetSpawnBounds( PrefabFile prefab )
	{
		var resourcePath = prefab.ResourcePath ?? MoneyPrinterDefinition.DefaultResourcePath;
		if ( CachedBounds.TryGetValue( resourcePath, out var bounds ) )
			return bounds;

		bounds = prefab.GetScene().GetLocalBounds();
		CachedBounds[resourcePath] = bounds;
		return bounds;
	}

	static float GetYawCorrection( BBox bounds )
	{
		var size = bounds.Size;
		return size.x > size.y ? 90.0f : 0.0f;
	}

	static IEnumerable<GameObject> EnumerateSelfAndDescendants( GameObject root )
	{
		yield return root;

		foreach ( var child in root.Children )
		{
			foreach ( var nested in EnumerateSelfAndDescendants( child ) )
			{
				yield return nested;
			}
		}
	}
}
