﻿using Sandbox;
using Sandbox.UI;

[UseTemplate]
internal class GameMenu : NavigatorPanel
{

	public GameMenu()
	{
		Navigate( "menu/controls" );
	}

	public void Close()
	{
		SetClass( "open", false );
	}

	[Event.BuildInput]
	private void BuildInput( InputBuilder b )
	{
		if ( b.Pressed( InputButton.Score ) )
		{
			SetClass( "open", !HasClass( "open" ) );
		}
	}

}

