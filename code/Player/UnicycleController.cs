﻿using System;
using System.Numerics;
using Sandbox;
using Sandbox.ScreenShake;

internal partial class UnicycleController : BasePlayerController
{

	[ConVar.Replicated( "uf_debug_nofall" )]
	public static bool NoFall { get; set; } = false;

	[ConVar.Replicated( "uf_debug_notilt" )]
	public static bool NoTilt { get; set; } = false;

	public float PedalTime => .75f;
	public float PedalResetAfter => 1.5f;
	public float PedalResetTime => .75f;
	public float MinPedalStrength => 10f;
	public float MaxPedalStrength => 50f;
	public float MinJumpStrength => 200f;
	public float MaxJumpStrength => 375f;
	public float MaxJumpStrengthTime => 1f;
	public float PerfectPedalBoost => 50f;
	public float MaxLean => 42f;
	public float LeanSafeZone => 8f;
	public float LeanSpeed => 80f;
	public float TipSpeed => 1.5f;
	public float SlopeTipSpeed => 3.25f;
	public float GroundTurnSpeed => 2f;
	public float AirTurnSpeed => 2f;
	public float SlopeSpeed => 800f;
	public float BrakeStrength => 3f;
	public float StopSpeed => 10f;
	public float MaxAirTurnSpeed => 35f;
	// fuck it
	public float HowFastYouPitchWhenForwardVelocityChanges => 3f;
	public float HowFastYouRollWhenRightVelocityChanges => 1.5f;

	public string GroundSurface { get; private set; }

	private UnicyclePlayer pl => Pawn as UnicyclePlayer;
	public Vector3 Mins => new( -8, -8, 0 );
	public Vector3 Maxs => new( 8, 8, 48 );

	private UnicycleUnstuck unstuck;

	public UnicycleController()
	{
		unstuck = new( this );
	}

	public override void FrameSimulate()
	{
		base.FrameSimulate();

		EyeRot = Rotation.Identity;

		if ( Debug )
		{
			var intpos = new Vector3( (int)Position.x, (int)Position.y, (int)Position.z );
			DebugOverlay.Text( Position, "Speed: " + Velocity.Length );
			DebugOverlay.Text( Position + Vector3.Down * 3, "Position: " + intpos );
			DebugOverlay.Text( Position + Vector3.Down * 6, "Grounded: " + (GroundEntity != null) );
			DebugOverlay.Text( Position + Vector3.Down * 9, "GroundNormal: " + GroundNormal );
			DebugOverlay.Text( Position + Vector3.Down * 12, "Surface: " + GroundSurface );
			DebugOverlay.Text( Position + Vector3.Down * 15, "Water Level: " + Pawn.WaterLevel.Fraction );
			DebugOverlay.Text( Position + Vector3.Down * 18, "Tilt: " + pl.Tilt );

			DebugOverlay.Line( Position, Position + Velocity, Color.Yellow );
		}
	}

	public override void Simulate()
	{
		if ( pl == null || pl.Fallen ) return;
		if ( unstuck.TestAndFix() ) return;

		var beforeGrounded = GroundEntity != null;
		var beforeVelocity = Velocity;

		CheckGround();
		CheckPedal();
		CheckBrake();
		CheckJump();
		DoFriction();
		DoSlope();
		DoTilt();

		// lerp pedals into place, adding velocity and lean
		if ( pl.TimeSincePedalStart < pl.TimeToReachTarget + Time.Delta )
		{
			var a = pl.TimeSincePedalStart / pl.TimeToReachTarget;
			a = Easing.EaseOut( a );

			MovePedals( pl.PedalStartPosition.LerpTo( pl.PedalTargetPosition, a ) );
		}

		DoRotation();
		Gravity();

		// go
		Move();

		if ( ShouldFall() )
		{
			pl.Fall();

			AddEvent( "fall" );
		}

		pl.TimeSincePedalStart += Time.Delta;
		pl.PrevGrounded = beforeGrounded;
		pl.PrevVelocity = beforeVelocity;
	}

	private bool ShouldFall()
	{
		if ( NoFall ) return false;

		if ( GroundEntity != null && !pl.PrevGrounded )
		{
			if ( pl.PrevVelocity.z < -1000 )
				return true;
		}

		if ( pl.PrevVelocity.Length > StopSpeed )
		{
			var wallTrStart = Position;
			var wallTrEnd = wallTrStart + pl.PrevVelocity * Time.Delta;
			var tr = TraceBBox( wallTrStart, wallTrEnd, Mins + Vector3.Up * 16, Maxs );

			if ( tr.Hit && Vector3.GetAngle( tr.Normal, Vector3.Up ) > 85f )
			{
				var d = Vector3.Dot( tr.Normal, pl.PrevVelocity );
				if ( d < -.3f )
					return true;
			}
		}

		var ang = Rotation.Angles();
		var aroll = Math.Abs( ang.roll );
		var apitch = Math.Abs( ang.pitch );
		var maxLean = GroundEntity != null ? MaxLean : 180;

		if ( aroll > maxLean || apitch > maxLean )
			return true;

		if ( aroll + apitch > maxLean * 1.50f )
			return true;

		return false;
	}

	private void Move()
	{
		var mover = new MoveHelper( Position, Velocity );
		mover.Trace = mover.Trace.Size( Mins, Maxs ).Ignore( Pawn );
		mover.MaxStandableAngle = 75f;
		mover.TryMoveWithStep( Time.Delta, 12 );

		Position = mover.Position;
		Velocity = mover.Velocity;
	}

	private void DoSlope()
	{
		if ( GroundEntity == null ) return;

		var slopeAngle = Vector3.GetAngle( GroundNormal, Vector3.Up );
		if ( slopeAngle == 0 ) return;

		var heading = Vector3.Dot( GroundNormal, Rotation.Forward.WithZ( 0 ).Normal );
		var dir = Vector3.Cross( GroundNormal, Rotation.Right ).Normal;

		var left = Vector3.Cross( GroundNormal, Vector3.Up ).Normal;
		var slopeDir = Vector3.Cross( GroundNormal, left ).Normal;
		var strengthRatio = slopeAngle / 90f;
		var strength = SlopeSpeed * strengthRatio * Math.Abs( Vector3.Dot( dir, slopeDir ) );
		var velocityVector = dir * strength * Math.Sign( heading );

		Velocity += velocityVector * Time.Delta;

		if ( Debug )
		{
			DebugOverlay.Line( Position, Position + velocityVector, Color.Red );
		}
	}

	private void CheckGround()
	{
		var tr = TraceBBox( Position, Position + Vector3.Down * 4f, Mins, Maxs, 3f );

		if ( !tr.Hit || Vector3.GetAngle( Vector3.Up, tr.Normal ) < 5f && Velocity.z > 140f )
		{
			if ( GroundEntity != null )
			{
				pl.TimeSinceNotGrounded = 0;
			}
			GroundEntity = null;
			return;
		}

		if ( GroundEntity == null )
		{
			AddEvent( "land" );
			pl.Tilt = Rotation.Angles().WithYaw( 0 );
			Position = Position.WithZ( tr.EndPos.z );

			if ( !pl.JumpTilt.Length.AlmostEqual( 0, .1f ) )
			{
				if ( pl.PrevJumpTilt.Length > 35 )
				{
					pl.JumpTilt = pl.PrevJumpTilt * .5f;
					pl.Tilt = pl.PrevJumpTilt * -1f;
				}
				else
				{
					pl.JumpTilt = Angles.Zero;
					pl.Tilt = Angles.Zero;
				}
			}

		}

		GroundEntity = tr.Entity;
		GroundNormal = tr.Normal;
		GroundSurface = tr.Surface.Name;
	}

	private void Gravity()
	{
		if ( GroundEntity != null )
		{
			if ( Vector3.GetAngle( GroundNormal, Vector3.Up ) <= 5 )
			{
				Velocity = Velocity.WithZ( 0 );
			}
			return;
		}

		Velocity -= new Vector3( 0, 0, 800f * Time.Delta );
	}

	private void DoFriction()
	{
		if ( GroundEntity == null ) return;

		var speed = Velocity.Length;
		if ( speed < 0.1f ) return;

		var drop = speed * Time.Delta * .1f * GetSurfaceFriction();
		var newspeed = Math.Max( speed - drop, 0 );

		if ( newspeed != speed )
		{
			newspeed /= speed;
			Velocity *= newspeed;
		}
	}

	private void DoTilt()
	{
		if ( NoTilt )
		{
			pl.Tilt = Angles.Zero;
			return;
		}

		// recover tilt from momentum
		var recover = Math.Min( Velocity.WithZ( 0 ).Length / 100f, 2.25f );
		var tilt = pl.Tilt;
		tilt = Angles.Lerp( tilt, Angles.Zero, recover * Time.Delta );

		// tilt from input
		var input = new Vector3( Input.Forward, 0, -Input.Left );
		tilt += new Angles( input.x, 0, input.z ) * LeanSpeed * Time.Delta;

		// continue to tip if not in the safe zone, but not when
		// jump tilt is doing its thing or when trying to manually tilt
		var len = tilt.Length;
		if( len > LeanSafeZone )
		{
			if ( GroundEntity != null && pl.JumpTilt.Length.AlmostEqual( 0f, .05f ) && input.Length.AlmostEqual( 0 ) )
			{
				var t = len / MaxLean;
				var str = .25f.LerpTo( TipSpeed, t );
				tilt += tilt * str * Time.Delta;
			}
		}

		// tilt from changes in velocity
		if ( GroundEntity != null )
		{
			var velDiff = pl.Transform.NormalToLocal( Velocity - pl.PrevVelocity );

			// cancel out a bit of the pitch if we're turning sharp
			if ( Math.Abs( velDiff.y ) > Math.Abs( velDiff.x ) ) velDiff.x *= .25f;

			tilt += new Angles( -velDiff.x * HowFastYouPitchWhenForwardVelocityChanges, 0, velDiff.y * HowFastYouRollWhenRightVelocityChanges ) * Time.Delta;
		}

		// tilt while on uneven ground
		if( GroundEntity != null && Vector3.GetAngle(GroundNormal, Vector3.Up) > 2f )
		{
			var groundRot = FromToRotation( Vector3.Up, !NoTilt ? GroundNormal : Vector3.Up );
			groundRot *= pl.TargetForward;
			var groundTilt = groundRot.Angles().WithYaw( 0 );

			tilt += groundTilt * SlopeTipSpeed * Time.Delta;
		}

		// this handles how we tilt and recover tilt after jumping
		tilt += pl.JumpTilt.Normal * 3f * Time.Delta;

		if ( GroundEntity != null )
		{
			var before = pl.JumpTilt;
			pl.JumpTilt = Angles.Lerp( pl.JumpTilt, Angles.Zero, 3f * Time.Delta );
			var delta = before - pl.JumpTilt;
			tilt += delta;
		}

		if ( IsIdle() )
		{
			// Jake: I'm using t in a silly way here
			//		 I don't want it to lerp from A to B by T because
			//		 we can't really snap the tilt like that without
			//		 conflicting with the player's input, so lerp w/
			//		 delta and use t for easing
			var rndTilt = GetRandomTilt( out float t );
			tilt = Angles.Lerp( tilt, rndTilt, 1.75f * Time.Delta * t );
		}

		tilt.roll = Math.Clamp( tilt.roll, -MaxLean - 5, MaxLean + 5 );
		tilt.pitch = Math.Clamp( tilt.pitch, -MaxLean - 5, MaxLean + 5 );

		pl.Tilt = tilt;
	}

	private bool IsIdle()
	{
		var input = new Vector3( Input.Forward, 0, -Input.Left );

		if ( GroundEntity == null ) return false;
		if ( !input.Length.AlmostEqual( 0f ) ) return false;
		if ( !pl.JumpTilt.Length.AlmostEqual( 0f ) ) return false;
		if ( pl.TimeSincePedalStart < PedalTime ) return false;
		if ( pl.Tilt.Length >= LeanSafeZone ) return false;
		if ( Input.Down( InputButton.Duck ) && Velocity.Length > StopSpeed ) return false;

		return true;
	}

	private Angles GetRandomTilt( out float t )
	{
		// this should give a deterministic random tilt to avoid
		// having to net sync additional stuff

		var seed = pl.NetworkIdent + ( Time.Tick / 50f );
		t = seed - (int)seed;

		Rand.SetSeed( (int)seed );
		var newRnd = Angles.Random.WithYaw( 0 ).Normal;

		// can do something like this to avoid repetitive randoms
		//Rand.SetSeed( (int)seed - 1 );
		//var prevRnd = Angles.Random.WithYaw( 0 ).Normal;
		//if ( Math.Sign( newRnd.pitch ) == Math.Sign( prevRnd.pitch ) ) newRnd.pitch *= -1;
		//if ( Math.Sign( newRnd.roll ) == Math.Sign( prevRnd.roll ) ) newRnd.roll *= -1;

		return newRnd * LeanSafeZone * .75f;
	}

	private void DoRotation()
	{
		var spd = Velocity.WithZ( 0 ).Length;
		var grounded = GroundEntity != null;
		var inputFwd = Input.Rotation.Forward.WithZ( 0 );

		var canTurn = (!grounded && spd < MaxAirTurnSpeed) || (grounded && spd > StopSpeed);

		if ( canTurn )
		{
			var inputRot = FromToRotation( Vector3.Forward, inputFwd );
			var turnSpeed = grounded ? GroundTurnSpeed : AirTurnSpeed;
			pl.TargetForward = Rotation.Slerp( pl.TargetForward, inputRot, turnSpeed * Time.Delta );

			Velocity = ClipVelocity( Velocity, pl.TargetForward.Right );
		}

		//var targetRot = FromToRotation( Vector3.Up, !NoTilt ? GroundNormal : Vector3.Up );
		var targetRot = pl.TargetForward;
		targetRot *= Rotation.From( pl.Tilt );
		Rotation = Rotation.Slerp( Rotation, targetRot, 6.5f * Time.Delta );

		// zero velocity if on flat ground and moving slow
		if ( grounded && Velocity.Length < StopSpeed && Vector3.GetAngle( Vector3.Up, GroundNormal ) < 5f )
		{
			Velocity = 0;
		}
	}

	private void CheckJump()
	{
		if ( Input.Released( InputButton.Jump ) && 
			(GroundEntity != null || pl.TimeSinceNotGrounded < .25f) )
		{
			var t = Math.Min( pl.TimeSinceJumpDown / MaxJumpStrengthTime, 1f );
			t = Easing.EaseOut( t );
			var jumpStrength = MinJumpStrength.LerpTo( MaxJumpStrength, t );
			var up = Rotation.From( Rotation.Angles() ).Up;

			pl.JumpTilt = pl.Tilt * -1;
			pl.PrevJumpTilt = pl.JumpTilt;
			Velocity = Velocity.WithZ( 0 );
			Velocity += up * jumpStrength;
			GroundEntity = null;
			pl.TimeSinceJumpDown = 0;

			AddEvent( "jump" );
			return;
		}

		if ( !CanIncrementJump() || !Input.Down( InputButton.Jump ) )
		{
			pl.TimeSinceJumpDown = 0;
			return;
		}

		if ( Input.Down( InputButton.Jump ) )
		{
			pl.TimeSinceJumpDown += Time.Delta;
		}
	}

	private bool CanIncrementJump()
	{
		if ( GroundEntity != null ) return true;
		if ( pl.TimeSinceNotGrounded < .75f ) return true;

		var tr = TraceBBox( Position, Position + Vector3.Down * 75, 5f );

		if ( !tr.Hit ) return false;

		return true;
	}

	private void CheckPedal()
	{
		if ( !pl.PedalPosition.AlmostEqual( 0f, .1f ) && pl.TimeSincePedalStart > PedalResetAfter )
		{
			SetPedalTarget( 0f, PedalResetTime );
			return;
		}

		if ( Input.Pressed( InputButton.Attack1 ) && pl.PedalPosition >= -.4f )
			SetPedalTarget( -1f, PedalTime, true );

		if ( Input.Pressed( InputButton.Attack2 ) && pl.PedalPosition <= .4f )
			SetPedalTarget( 1f, PedalTime, true );
	}

	private void CheckBrake()
	{
		if ( GroundEntity == null ) return;
		if ( !Input.Down( InputButton.Duck ) ) return;

		if ( !pl.PrevGrounded && Velocity.WithZ( 0 ).Length < 300 )
		{
			Velocity *= .35f;
		}

		Velocity = Velocity.LerpTo( Vector3.Zero, Time.Delta * BrakeStrength ).WithZ( Velocity.z );
	}

	private void SetPedalTarget( float target, float timeToReach, bool tryBoost = false )
	{
		var prevStart = pl.PedalPosition;
		var prevStartTime = pl.TimeSincePedalStart;

		pl.TimeSincePedalStart = 0;
		pl.TimeToReachTarget = timeToReach;
		pl.PedalStartPosition = pl.PedalPosition;
		pl.PedalTargetPosition = target;

		AddEvent( "pedal" );

		if ( !tryBoost ) return;
		if ( GroundEntity == null ) return;
		if ( Math.Abs( prevStart ) <= .95f ) return;
		if ( prevStartTime > PedalTime ) return;

		if ( Pawn.IsLocalPawn )
		{
			new Perlin();
			// sound + particle
		}

		Velocity += Rotation.Forward.WithZ( 0 ) * PerfectPedalBoost;
	}

	private void MovePedals( float newPosition )
	{
		var delta = newPosition - pl.PedalPosition;
		pl.PedalPosition = newPosition;

		// don't add velocity when pedals are returning to idle or in air..
		if ( pl.PedalTargetPosition == 0 ) return;
		if ( GroundEntity == null ) return;

		var strengthAlpha = Math.Abs( pl.PedalStartPosition );
		var strength = MinPedalStrength.LerpTo( MaxPedalStrength, strengthAlpha );
		var addVelocity = Rotation.Forward * strength * Math.Abs( delta );

		pl.Tilt += new Angles( 0, 0, 15f * delta );
		Velocity += addVelocity;

		if ( !Velocity.Length.AlmostEqual( 0 ) && Velocity.Length < StopSpeed )
		{
			Velocity *= StopSpeed / Velocity.Length;
		}
	}

	private float GetSurfaceFriction()
	{
		// todo: snow, gravel
		return GroundSurface switch
		{
			"mud" => 5.0f,
			"sand" => 20.0f,
			"dirt" => 2.0f,
			_ => 1.0f,
		};
	}

	static Rotation FromToRotation( Vector3 aFrom, Vector3 aTo )
	{
		Vector3 axis = Vector3.Cross( aFrom, aTo );
		float angle = Vector3.GetAngle( aFrom, aTo );
		return AngleAxis( angle, axis.Normal );
	}

	static Rotation AngleAxis( float aAngle, Vector3 aAxis )
	{
		aAxis = aAxis.Normal;
		float rad = aAngle * MathX.DegreeToRadian( .5f );
		aAxis *= MathF.Sin( rad );
		return new Quaternion( aAxis.x, aAxis.y, aAxis.z, MathF.Cos( rad ) );
	}

	static Vector3 ClipVelocity( Vector3 vel, Vector3 norm, float overbounce = 1.0f )
	{
		var backoff = Vector3.Dot( vel, norm ) * overbounce;
		var o = vel - (norm * backoff);

		// garry: I don't totally understand how we could still
		//		  be travelling towards the norm, but the hl2 code
		//		  does another check here, so we're going to too.
		var adjust = Vector3.Dot( o, norm );
		if ( adjust >= 1.0f ) return o;

		adjust = MathF.Min( adjust, -1.0f );
		o -= norm * adjust;

		return o;
	}

}
