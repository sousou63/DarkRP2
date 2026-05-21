/// <summary>
/// Sniper viewmodel helper. Moves the weapon down during scope transitions.
/// </summary>
public sealed class SniperViewModel : Component, ICameraSetup
{
	[Property] public float LowerAmount { get; set; } = 1.5f;
	[Property] public float LowerSpeed { get; set; } = 10f;

	private float _offset;

	void ICameraSetup.PostSetup( CameraComponent cc )
	{
		var weapon = GetComponentInParent<SniperWeapon>();
		if ( !weapon.IsValid() ) return;

		// Move the gun down while transitioning in/out of scope
		var target = weapon.IsScoped ? LowerAmount : 0f;
		_offset = _offset.LerpTo( target, Time.Delta * LowerSpeed );

		if ( _offset > 0.01f )
		{
			WorldPosition += cc.WorldRotation.Down * _offset;
		}
	}
}
