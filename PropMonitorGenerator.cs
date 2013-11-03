using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace RasterPropMonitorGenerator
{
	public class RasterPropMonitorGenerator: InternalModule
	{
		[KSPField]
		public int refreshRate = 5;
		[KSPField]
		public int refreshDataRate = 10;
		// I wish I could get rid of this particular mess, because in theory I can support an unlimited number of pages.
		[KSPField]
		public string page1 = "Display$$$ not$$$  configured.";
		[KSPField]
		public string button1 = "";
		[KSPField]
		public string page2 = "";
		[KSPField]
		public string button2 = "";
		[KSPField]
		public string page3 = "";
		[KSPField]
		public string button3 = "";
		[KSPField]
		public string page4 = "";
		[KSPField]
		public string button4 = "";
		[KSPField]
		public string page5 = "";
		[KSPField]
		public string button5 = "";
		[KSPField]
		public string page6 = "";
		[KSPField]
		public string button6 = "";
		[KSPField]
		public string page7 = "";
		[KSPField]
		public string button7 = "";
		[KSPField]
		public string page8 = "";
		[KSPField]
		public string button8 = "";
		// Config syntax.
		private string[] lineSeparator = { Environment.NewLine };
		private string[] variableListSeparator = { "$&$" };
		//private string[] variableSeparator = { "|" };
		private string[] variableSeparator = {};
		private InternalModule targetScript;
		private string[] textArray;
		// Important pointers to the screen's data structures.
		FieldInfo remoteArray;
		FieldInfo remoteFlag;
		// Local variables
		private string[] pages = { "", "", "", "", "", "", "", "" };
		private int activePage = 0;
		private int charPerLine = 23;
		private int linesPerPage = 17;
		private int updateCountdown = 0;
		private int dataUpdateCountdown = 0;
		private bool updateForced = false;
		private bool screenWasBlanked = false;
		private bool currentPageIsMutable = false;
		private bool currentPageFirstPassComplete = false;
		private int vesselNumParts;
		// All computations are split into a separate class, because it was getting a mite too big.
		public RasterPropMonitorComputer comp;

		public void Start ()
		{
			// Mihara: We're getting at the screen module and it's parameters using reflection here.
			// While I would prefer to use some message passing mechanism instead,
			// it does not look like I can use KSPEvent.
			// I could directly lock at the parameters, seeing as how these two modules
			// are in the same assembly, but instead I'm leaving the reflection-based mechanism here
			// so that you could make your own screen driver module
			// by simply copy-pasting the relevant sections.
			//
			// Once you have that you're golden -- you can populate the array of lines,
			// and trigger the screen update by writing a boolean when it needs updating.
			foreach (InternalModule intModule in base.internalProp.internalModules) {
				if (intModule.ClassName == "RasterPropMonitor") {
					targetScript = intModule;
					remoteArray = intModule.GetType ().GetField ("screenText");
					remoteFlag = intModule.GetType ().GetField ("screenUpdateRequired");

					charPerLine = (int)intModule.GetType ().GetField ("screenWidth").GetValue (intModule);
					linesPerPage = (int)intModule.GetType ().GetField ("screenHeight").GetValue (intModule);

					break;
				}
			}

			// Everything from there on is just my idea of doing it and can be done in a myriad different ways.

			string[] pageData = new string[] { page1, page2, page3, page4, page5, page6, page7, page8 };
			string[] buttonName = new string[] { button1, button2, button3, button4, button5, button6, button7, button8 };
			for (int i=0; i<8; i++) {
				//Debug.Log ("RasterMonitor: Page " + i.ToString () + " data is \"" + pageData [i] + "\" button name is " + buttonName [i]);
				if (buttonName [i] != "") {
					GameObject buttonObject = base.internalProp.FindModelTransform (buttonName [i]).gameObject;
					buttonHandler pageButton = buttonObject.AddComponent<buttonHandler> ();
					pageButton.ID = i;
					pageButton.handlerFunction = buttonClick;
				}

				try {
					pages [i] = String.Join (Environment.NewLine, File.ReadAllLines (KSPUtil.ApplicationRootPath + "GameData/" + pageData [i], System.Text.Encoding.UTF8));
				} catch {
					// Notice that this will also happen if the referenced file is not found.
					pages [i] = pageData [i].Replace ("<=", "{").Replace ("=>", "}").Replace ("$$$", Environment.NewLine);
				}
			}


			textArray = new string[linesPerPage];
			for (int i = 0; i < textArray.Length; i++) {
				textArray [i] = "";
			}


			// Until I know how to limit this search to a single capsule, I'll have to put it off.
			// Note for the future. InternalModel is a feature of a Part.
			// Which means I should be able to limit it to a single capsule by traversing the part tree --
			// if I can identify the InternalModel which contains the prop this module is attached to
			// when looking through parts, this should be feasible.

			/*
			foreach (InternalProp other in FindObjectsOfType (typeof(InternalProp)) as InternalProp[]) {
				if (other.vessel == FlightGlobals.ActiveVessel) {
					RasterPropMonitorGenerator othermodule = other.FindModelComponent<RasterPropMonitorGenerator> ();
					if (othermodule != null && othermodule.comp != null) {
						// Not going further with that until I know what happens when two IVAs separate.
						comp = othermodule.comp;
						Debug.Log ("RasterPropMonitorGenerator: Found an existing calculator instance, using that.");
					}
				}
			}
			*/
			if (comp == null) {
				Debug.Log ("RasterPropMonitorGenerator: Instantiating a new calculator.");
				comp = new RasterPropMonitorComputer ();
			}

		}

		public void buttonClick (int buttonID)
		{
			activePage = buttonID;
			updateForced = true;
			currentPageIsMutable = false;
			currentPageFirstPassComplete = false;
		}

		private string processString (string input)
		{
			// Each separate output line is delimited by Environment.NewLine.
			// When loading from a config file, you can't have newlines in it, so they're represented by "$$$".
			//
			// Within each line, if it contains any variables, it contains String.Format's format codes:
			// "Insert {0:0.0} variables {0:0.0} into this string###VARIABLE|VARIABLE"
			// 
			// <= has to be substituted for { and => for } when defining a screen in a config file.
			// It is much easier to write a text file and reference it by URL instead, writing 
			// screen definitions in a config file is only good enough for very small screens.
			// 
			// A more readable string format reference detailing where each variable is to be inserted and 
			// what it should look like can be found here: http://blog.stevex.net/string-formatting-in-csharp/

			if (input.IndexOf (variableListSeparator [0]) >= 0) {
				currentPageIsMutable = true;

				string[] tokens = input.Split (variableListSeparator, StringSplitOptions.RemoveEmptyEntries);
				if (tokens.Length != 2) {
					return "FORMAT ERROR";
				} else {
					string[] vars = tokens [1].Split (variableSeparator, StringSplitOptions.RemoveEmptyEntries);

					object[] variables = new object[vars.Length];
					for (int i=0; i<vars.Length; i++) {
						//Debug.Log ("PropMonitorGenerator: Processing " + vars[i]);
						variables [i] = comp.processVariable (vars [i]);
					}
					return String.Format (tokens [0], variables);
				}
			} else
				return input;
		}
		// Update according to the given refresh rate or when number of parts changes.
		private bool updateCheck ()
		{
			if (vesselNumParts != vessel.Parts.Count || updateCountdown <= 0 || dataUpdateCountdown <= 0 || updateForced) {
				updateCountdown = refreshRate;
				if (vesselNumParts != vessel.Parts.Count || dataUpdateCountdown <= 0) {
					dataUpdateCountdown = refreshDataRate;
					vesselNumParts = vessel.Parts.Count;
					if (currentPageIsMutable || !currentPageFirstPassComplete) {
						comp.fetchPerPartData ();
					}
				}
				updateForced = false;
				return true;
			} else {
				dataUpdateCountdown--;
				updateCountdown--;
				return false;
			}
		}

		public override void OnUpdate ()
		{
			if (!HighLogic.LoadedSceneIsFlight)
				return;

			if (!updateCheck ())
				return;

			if ((CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA ||
			    CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.Internal) &&
			    vessel == FlightGlobals.ActiveVessel) {

				if (pages [activePage] == "") { // In case the page is empty, the screen is treated as turned off and blanked once.
					if (!screenWasBlanked) {
						for (int i = 0; i < textArray.Length; i++)
							textArray [i] = "";
						screenWasBlanked = true;
						remoteArray.SetValue (targetScript, textArray);
						remoteFlag.SetValue (targetScript, true);
					}
				} else {
					if (!currentPageFirstPassComplete || currentPageIsMutable) {
						comp.fetchCommonData (); // Doesn't seem to be a better place to do it in...

						string[] linesArray = pages [activePage].Split (lineSeparator, StringSplitOptions.None);
						for (int i=0; i<linesPerPage; i++) {
							if (i < linesArray.Length) {
								textArray [i] = processString (linesArray [i]).TrimEnd();
							} else
								textArray [i] = "";
						}
						remoteArray.SetValue (targetScript, textArray);
						remoteFlag.SetValue (targetScript, true);
						screenWasBlanked = false;
						currentPageFirstPassComplete = true;
					}
				}

			}
		}
	}

	public class buttonHandler:MonoBehaviour
	{
		public delegate void HandlerFunction (int ID);

		public HandlerFunction handlerFunction;
		public int ID;

		public void OnMouseDown ()
		{
			handlerFunction (ID);
		}
	}
}

