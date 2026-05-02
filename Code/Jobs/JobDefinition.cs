[AssetType( Name = "DarkRP Job", Extension = "jobdef", Category = "DarkRP", Flags = AssetTypeFlags.NoEmbedding | AssetTypeFlags.IncludeThumbnails )]
public sealed class JobDefinition : GameResource, IDefinitionResource
{
	public const string DefaultResourcePath = "jobs/citizen.jobdef";

	[Property]
	public string Title { get; set; }

	[Property]
	public string Description { get; set; }

	[Property]
	public string Category { get; set; } = "Civilian";

	[Property]
	public Color AccentColor { get; set; } = Color.Transparent;

	[Property]
	public int Salary { get; set; } = 45;

	[Property]
	public int MaxPlayers { get; set; }

	[Property]
	public bool RequiresVote { get; set; }

	[Property]
	public string Command { get; set; }

	[Property]
	public int Order { get; set; }

	[Property]
	public string[] StartingItems { get; set; } = [];

	[Property]
	public bool UseOwnerAvatarAppearance { get; set; }

	[Property]
	public bool PreserveOwnerAvatarAppearance { get; set; } = true;

	[Property]
	public string[] Clothing { get; set; } = [];

	[Property]
	public bool IsDefault { get; set; }

	public static IReadOnlyList<JobDefinition> GetAll()
	{
		return ResourceLibrary.GetAll<JobDefinition>()
			.OrderBy( x => x.Order )
			.ThenBy( x => x.Category )
			.ThenBy( x => x.Title )
			.ToArray();
	}

	public static JobDefinition Get( string resourcePath )
	{
		if ( string.IsNullOrWhiteSpace( resourcePath ) )
			return null;

		return ResourceLibrary.Get<JobDefinition>( resourcePath );
	}

	public static JobDefinition GetDefault()
	{
		return Get( DefaultResourcePath )
			?? GetAll().FirstOrDefault( x => x.IsDefault )
			?? GetAll().FirstOrDefault();
	}

	public Color GetDisplayColor()
	{
		if ( AccentColor.a > 0f )
			return AccentColor;

		return Category?.Trim() switch
		{
			"Government" => new Color( 0.4549f, 0.7529f, 0.9882f ),
			"Services" => new Color( 0.3882f, 0.9020f, 0.7451f ),
			"Commerce" => new Color( 1.0000f, 0.8196f, 0.4000f ),
			"Criminal" => new Color( 1.0000f, 0.5294f, 0.5294f ),
			_ => new Color( 0.8471f, 0.9098f, 1.0000f )
		};
	}

	protected override Bitmap CreateAssetTypeIcon( int width, int height )
	{
		return CreateSimpleAssetTypeIcon( "👔", width, height, "#3b82f6" );
	}
}
