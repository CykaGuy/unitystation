﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using Random = UnityEngine.Random;

public partial class PlayerNetworkActions : NetworkBehaviour
{
	private readonly string[] slotNames = {
		"suit",
		"belt",
		"feet",
		"head",
		"mask",
		"uniform",
		"neck",
		"ear",
		"eyes",
		"hands",
		"id",
		"back",
		"rightHand",
		"leftHand",
		"storage01",
		"storage02",
		"suitStorage"
	};

	// For access checking. Must be nonserialized.
	// This has to be added because using the UIManager at client gets the server's UIManager. So instead I just had it send the active hand to be cached at server.
	[NonSerialized] public string activeHand = "rightHand";

	private ChatIcon chatIcon;

	private Equipment equipment;
	private PlayerMove playerMove;
	private PlayerScript playerScript;
	private PlayerSprites playerSprites;
	private RegisterTile registerTile;

	private SoundNetworkActions soundNetworkActions;

	public Dictionary<string, InventorySlot> Inventory { get; } = new Dictionary<string, InventorySlot>();

	public bool isGhost;

	private void Start()
	{
		equipment = GetComponent<Equipment>();
		playerMove = GetComponent<PlayerMove>();
		playerSprites = GetComponent<PlayerSprites>();
		playerScript = GetComponent<PlayerScript>();
		soundNetworkActions = GetComponent<SoundNetworkActions>();
		chatIcon = GetComponentInChildren<ChatIcon>();
		registerTile = GetComponentInParent<RegisterTile>();
	}

	public override void OnStartServer()
	{
		if (isServer)
		{
			List<InventorySlot> initSync = new List<InventorySlot>();
			foreach (string slotName in slotNames)
			{
				var invSlot =  new InventorySlot(Guid.NewGuid(), slotName, true);
				Inventory.Add(slotName, invSlot);
				InventoryManager.AllServerInventorySlots.Add(invSlot);
				initSync.Add(invSlot);
			}

			SyncPlayerInventoryGuidMessage.Send(gameObject, initSync);
		}

		base.OnStartServer();
	}

	[Server]
	public bool AddItem(GameObject itemObject, string slotName = null, bool replaceIfOccupied = false, bool forceInform = true)
	{
		string eventName = slotName ?? UIManager.Hands.CurrentSlot.eventName;
		if (Inventory[eventName] != null && Inventory[eventName].Item != itemObject && !replaceIfOccupied)
		{
			Logger.Log($"{gameObject.name}: Didn't replace existing {eventName} item {Inventory[eventName].Item.name} with {itemObject.name}", Category.Inventory);
			return false;
		}

		EquipmentPool.AddGameObject(gameObject, itemObject);
		SetInventorySlot(slotName, itemObject);
		UpdateSlotMessage.Send(gameObject, eventName, itemObject, forceInform);
		return true;
	}

	[Server]
	public void PlaceInSlot(GameObject item, string slotName)
	{
		UIManager.InventorySlots.GetSlotByEvent(slotName).SetItem(item);
	}

	private void PlaceInHand(GameObject item)
	{
		UIManager.Hands.CurrentSlot.SetItem(item);
	}

	/// Destroys item if it's in player's pool.
	/// It's not recommended to destroy shit in general due to the specifics of our game
	[Server]
	public void Consume(GameObject item)
	{
		foreach (var slot in Inventory)
		{
			if (item == slot.Value.Item)
			{
				ClearInventorySlot(slot.Key);
				break;
			}
		}
		EquipmentPool.DisposeOfObject(gameObject, item);
	}

	/// Checks if player has this item in any of his slots
	[Server]
	public bool HasItem(GameObject item)
	{
		foreach (var slot in Inventory)
		{
			if (item == slot.Value.Item)
			{
				return true;
			}
		}
		return false;
	}

	[Server]
	private void RemoveFromEquipmentPool(GameObject obj)
	{
		EquipmentPool.DropGameObject(gameObject, obj);
	}

	//This is for objects that aren't picked up via the hand (I.E a magazine clip inside a weapon that was picked up)
	//TODO make these private(make some public child-aware high level methods instead):
	[Server]
	public void AddToEquipmentPool(GameObject obj)
	{
		EquipmentPool.AddGameObject(gameObject, obj);
	}

	/// <summary>
	/// Validates the inv interaction.
	/// If you are not validating a drop action then pass Vector3.zero to dropWorldPos
	/// </summary>
	[Server]
	public bool ValidateInvInteraction(string slot, GameObject gObj = null, bool forceClientInform = true)
	{
		//security todo: serverside check for item size UI_ItemSlot.CheckItemFit()
		Debug.Log("!! FIXME: Contains Value needs to be rewritten to InventoryManager");
		if (!Inventory[slot].Item && gObj )//&& Inventory.ContainsValue(gObj))
		{
			UpdateSlotMessage.Send(gameObject, slot, gObj, forceClientInform);
			SetInventorySlot(slot, gObj);
			//Clean up other slots
			ClearObjectIfNotInSlot(gObj, slot, forceClientInform);
			Logger.LogTraceFormat("Approved moving {0} to slot {1}", Category.Inventory, gObj, slot);
			return true;
		}

		if (!gObj)
		{
			return ValidateDropItem(slot, forceClientInform);
		}

		Logger.LogWarning($"Unable to validateInvInteraction {slot}:{gObj.name}", Category.Inventory);
		return false;
	}

	public void RollbackPrediction(string slot)
	{
		UpdateSlotMessage.Send(gameObject, slot, Inventory[slot].Item, true);
	}

	[Server]
	private void ClearObjectIfNotInSlot(GameObject gObj, string slot, bool forceClientInform)
	{
		HashSet<string> toBeCleared = new HashSet<string>();
		foreach (string key in Inventory.Keys)
		{
			if (key.Equals(slot) || !Inventory[key].Item)
			{
				continue;
			}

			if (Inventory[key].Equals(gObj))
			{
				toBeCleared.Add(key);
			}
		}

		ClearInventorySlot(forceClientInform, toBeCleared.ToArray());
	}

	[Server]
	public void ClearInventorySlot(params string[] slotNames)
	{
		ClearInventorySlot(true, slotNames);
	}

	[Server]
	private void ClearInventorySlot(bool forceClientInform, params string[] slotNames)
	{
		for (int i = 0; i < slotNames.Length; i++)
		{
			Inventory[slotNames[i]] = null;
			if (slotNames[i] == "id" || slotNames[i] == "storage01" || slotNames[i] == "storage02" || slotNames[i] == "suitStorage")
			{
				//Not clearing onPlayer sprites for these as they don't have any
			}
			else
			{
				equipment.ClearItemSprite(slotNames[i]);
			}

			UpdateSlotMessage.Send(gameObject, slotNames[i], null, forceClientInform);
		}

		Logger.LogTraceFormat("Cleared {0}", Category.Inventory, slotNames);
	}

	[Server]
	public void SetInventorySlot(string slotName, GameObject obj)
	{
		Inventory[slotName].Item = obj;
		ItemAttributes att = obj.GetComponent<ItemAttributes>();

		if (slotName == "leftHand" || slotName == "rightHand")
		{
			equipment.SetHandItemSprite(att);
		}
		else
		{
			if (slotName == "id" || slotName == "storage01" || slotName == "storage02" || slotName == "suitStorage")
			{
				//Not setting onPlayer sprites for these as they don't have any
			}
			else
			{
				if (att.spriteType == SpriteType.Clothing || att.hierarchy.Contains("headset") ||
					att.hierarchy.Contains("storage/backpack") || att.hierarchy.Contains("storage/bag")
					|| att.hierarchy.Contains("storage/belt") || att.hierarchy.Contains("tank"))
				{
					Epos enumA = (Epos)Enum.Parse(typeof(Epos), slotName);
					equipment.syncEquipSprites[(int)enumA] = att.clothingReference;
				}
			}
		}
	}

	/// Drop an item from a slot. use forceSlotUpdate=false when doing clientside prediction, 
	/// otherwise client will forcefully receive update slot messages
	public void RequestDropItem(string hand, bool forceClientInform = true)
	{
		InventoryInteractMessage.Send(hand, null, forceClientInform);
	}

	//Dropping from a slot on the UI
	[Server]
	public bool ValidateDropItem(string slot, bool forceClientInform /* = false*/ )
	{
		//decline if not dropped from hands?
		if (Inventory.ContainsKey(slot) && Inventory[slot].Item)
		{
			DropItem(slot, forceClientInform);
			return true;
		}

		Logger.Log("Object not found in Inventory", Category.Inventory);
		return false;
	}

	///     Imperative drop.
	/// Pass empty slot to drop a random one
	[Server]
	public void DropItem(string slot = "", bool forceClientInform = true)
	{
		//Drop random item
		if (slot == "")
		{
			slot = "uniform";
			foreach (var key in Inventory.Keys)
			{
				if (Inventory[key].Item)
				{
					slot = key;
					break;
				}
			}
		}
		EquipmentPool.DropGameObject(gameObject, Inventory[slot].Item);
		Inventory[slot] = null;
		equipment.ClearItemSprite(slot);
		UpdateSlotMessage.Send(gameObject, slot, null, forceClientInform);
	}

	//Drop all items. Use onQuit only if player has left server
	[Server]
	public void DropAll(bool onQuit = false)
	{
		//Dropping whatever player has got
		if (!onQuit)
		{
			//fixme: modified collectionz
			foreach (var key in Inventory.Keys)
			{
				if (Inventory[key].Item)
				{
					DropItem(key);
				}
			}
		}
		else
		// Drop all shit from player's inventory when he's leaving, ignoring pools
		{
			foreach (var item in Inventory.Values)
			{
				if (!item.Item)
				{
					continue;
				}

				var objTransform = item.Item.GetComponent<CustomNetTransform>();
				if (objTransform)
				{
					objTransform.ForceDrop(gameObject.transform.position);
				}
			}
		}
	}

	/// Client requesting throw to clicked position
	[Command]
	public void CmdRequestThrow(string slot, Vector3 worldTargetPos, int aim)
	{
		if (playerScript.canNotInteract() || slot != "leftHand" && slot != "rightHand" || !SlotNotEmpty(slot))
		{
			RollbackPrediction(slot);
			return;
		}
		GameObject throwable = Inventory[slot].Item;

		Vector3 playerPos = playerScript.PlayerSync.ServerState.WorldPosition;

		EquipmentPool.DisposeOfObject(gameObject, throwable);
		ClearInventorySlot(slot);
		var throwInfo = new ThrowInfo
		{
			ThrownBy = gameObject,
				Aim = (BodyPartType)aim,
				OriginPos = playerPos,
				TargetPos = worldTargetPos,
				//Clockwise spin from left hand and Counterclockwise from the right hand
				SpinMode = slot == "leftHand" ? SpinMode.Clockwise : SpinMode.CounterClockwise,
		};
		throwable.GetComponent<CustomNetTransform>().Throw(throwInfo);

		//Simplified counter-impulse for players in space
		if (playerScript.PlayerSync.IsInSpace)
		{
			playerScript.PlayerSync.Push(Vector2Int.RoundToInt(-throwInfo.Trajectory.normalized));
		}
	}

	//Dropping from somewhere else in the players equipmentpool (Magazine ejects from weapons etc)
	[Command]
	[Obsolete]
	public void CmdDropItemNotInUISlot(GameObject obj)
	{
		EquipmentPool.DropGameObject(gameObject, obj);
	}

	public void DisposeOfChildItem(GameObject obj)
	{
		EquipmentPool.DisposeOfObject(gameObject, obj);
	}

	[Command] //Remember with the parent you can only send networked objects:
	public void CmdPlaceItem(string slotName, Vector3 pos, GameObject newParent, bool isTileMap)
	{
		if (!SlotNotEmpty(slotName))
		{
			return;
		}

		GameObject item = Inventory[slotName].Item;
		EquipmentPool.DropGameObject(gameObject, Inventory[slotName].Item, pos);
		ClearInventorySlot(slotName);
		if (item != null && newParent != null)
		{
			if (isTileMap)
			{
				var tileChangeManager = newParent.GetComponent<TileChangeManager>();
				item.transform.parent = tileChangeManager.ObjectParent.transform;
			}
			else
			{
				item.transform.parent = newParent.transform;
			}
			// TODO
			//			ReorderGameobjectsOnTile(pos);
		}
	}

	//	private void ReorderGameobjectsOnTile(Vector2 position)
	//	{
	//		List<RegisterItem> items = registerTile.Matrix.Get<RegisterItem>(position.RoundToInt()).ToList();
	//
	//		for (int i = 0; i < items.Count; i++)
	//		{
	//			SpriteRenderer sRenderer = items[i].gameObject.GetComponentInChildren<SpriteRenderer>();
	//			if (sRenderer != null)
	//			{
	//				sRenderer.sortingOrder = (i + 1);
	//			}
	//		}
	//	}

	public bool SlotNotEmpty(string eventName)
	{
		return Inventory.ContainsKey(eventName) && Inventory[eventName].Item != null;
	}

	[Command]
	public void CmdToggleCupboard(GameObject cupbObj)
	{
		ClosetControl closetControl = cupbObj.GetComponent<ClosetControl>();
		closetControl.ServerToggleCupboard();
	}

	[Command]
	public void CmdStartMicrowave(string slotName, GameObject microwave, string mealName)
	{
		Microwave m = microwave.GetComponent<Microwave>();
		m.ServerSetOutputMeal(mealName);
		ClearInventorySlot(slotName);
		m.RpcStartCooking();
	}

	[Command]
	public void CmdRequestJob(JobType jobType)
	{
		// Already have a job buddy!
		if (playerScript.JobType != JobType.NULL)
		{
			return;
		}

		playerScript.JobType = GameManager.Instance.GetRandomFreeOccupation(jobType);
		RespawnPlayer();
		ForceJobListUpdateMessage.Send();
	}

	[Command]
	public void CmdToggleShutters(GameObject switchObj)
	{
		ShutterSwitchTrigger s = switchObj.GetComponent<ShutterSwitchTrigger>();
		if (s.IsClosed)
		{
			s.IsClosed = false;
		}
		else
		{
			s.IsClosed = true;
		}
	}

	[Command]
	public void CmdToggleLightSwitch(GameObject switchObj)
	{
		LightSwitchTrigger s = switchObj.GetComponent<LightSwitchTrigger>();
		s.isOn = !s.isOn;
	}

	[Command]
	public void CmdToggleFireCabinet(GameObject cabObj, bool forItemInteract, string currentSlotName)
	{
		FireCabinetTrigger c = cabObj.GetComponent<FireCabinetTrigger>();

		if (!forItemInteract)
		{
			if (c.IsClosed)
			{
				c.IsClosed = false;
			}
			else
			{
				c.IsClosed = true;
			}
		}
		else
		{
			if (c.isFull)
			{
				c.isFull = false;
				if (AddItem(c.storedObject.gameObject, currentSlotName))
				{
					c.storedObject.visibleState = true;
					c.storedObject = null;
				}
			}
			else
			{
				c.storedObject = Inventory[currentSlotName].Item.GetComponent<ObjectBehaviour>();
				ClearInventorySlot(currentSlotName);
				c.storedObject.visibleState = false;
				c.isFull = true;
			}
		}
	}

	[Command]
	public void CmdMoveItem(GameObject item, Vector3 newPos)
	{
		item.transform.position = newPos;
	}

	[Server]
	public void SetConsciousState(bool conscious)
	{
		//Store the server conscious state for this player:
		playerScript.playerHealth.serverPlayerConscious = conscious;

		if (conscious)
		{
			playerMove.allowInput = true;
			playerSprites.RpcSetPlayerRot(0f);
		}
		else
		{
			playerMove.allowInput = false;
			playerSprites.RpcSetPlayerRot(-90f);
			soundNetworkActions.RpcPlayNetworkSound("Bodyfall", transform.position);
			if (Random.value > 0.5f)
			{
				playerSprites.currentDirection = Orientation.Up;
			}
		}
	}

	[Command]
	public void CmdToggleChatIcon(bool turnOn)
	{
		if (!GetComponent<VisibleBehaviour>().visibleState || (playerScript.JobType == JobType.NULL))
		{
			//Don't do anything with chat icon if player is invisible or not spawned in
			return;
		}

		RpcToggleChatIcon(turnOn);
	}

	[ClientRpc]
	private void RpcToggleChatIcon(bool turnOn)
	{
		if (!chatIcon)
		{
			chatIcon = GetComponentInChildren<ChatIcon>();
		}

		if (turnOn)
		{
			chatIcon.TurnOnTalkIcon();
		}
		else
		{
			chatIcon.TurnOffTalkIcon();
		}
	}

	[Command]
	public void CmdCommitSuicide()
	{
		GetComponent<HealthBehaviour>().ApplyDamage(gameObject, 1000, DamageType.BRUTE, BodyPartType.CHEST);
	}

	[ClientRpc]
	public void RpcSpawnGhost()
	{
		isGhost = true;
		playerScript.ghost.SetActive(true);
		playerScript.ghost.transform.parent = null;
		chatIcon.gameObject.transform.parent = playerScript.ghost.transform;
		playerScript.ghost.transform.rotation = Quaternion.identity;
		if (PlayerManager.LocalPlayer == gameObject)
		{
			SoundManager.Stop("Critstate");
			UIManager.PlayerHealthUI.heartMonitor.overlayCrits.SetState(OverlayState.death);
			Camera2DFollow.followControl.target = playerScript.ghost.transform;
			Camera2DFollow.followControl.damping = 0.0f;
			FieldOfView fovScript = GetComponent<FieldOfView>();
			if (fovScript != null)
			{
				fovScript.enabled = false;
			}

			//Show ghosts and hide FieldOfView
			var mask = Camera2DFollow.followControl.cam.cullingMask;
			mask |= 1 << LayerMask.NameToLayer("Ghosts");
			Camera2DFollow.followControl.cam.cullingMask = mask;
		}
	}

	//Respawn action for Deathmatch v 0.1.3

	[Server]
	public void RespawnPlayer(int timeout = 0)
	{
		if (GameManager.Instance.RespawnAllowed)
		{
			StartCoroutine(InitiateRespawn(timeout));
		}
	}

	[Server]
	private IEnumerator InitiateRespawn(int timeout)
	{
		//Debug.LogFormat("{0}: Initiated respawn in {1}s", gameObject.name, timeout);
		yield return new WaitForSeconds(timeout);
		RpcAdjustForRespawn();

		EquipmentPool.ClearPool(gameObject);

		SpawnHandler.RespawnPlayer(connectionToClient, playerControllerId, playerScript.JobType);
	}

	[ClientRpc]
	private void RpcAdjustForRespawn()
	{
		ClosetPlayerHandler cph = GetComponent<ClosetPlayerHandler>();
		if (cph != null)
		{
			Destroy(cph);
		}

		Camera2DFollow.followControl.damping = 0.0f;
		playerScript.ghost.SetActive(false);
		isGhost = false;
		//Hide ghosts and show FieldOfView

		var mask = Camera2DFollow.followControl.cam.cullingMask;
		mask &= ~(1 << LayerMask.NameToLayer("Ghosts"));
		Camera2DFollow.followControl.cam.cullingMask = mask;

		gameObject.GetComponent<InputController>().enabled = false;
	}

	//FOOD
	[Command]
	public void CmdEatFood(GameObject food, string fromSlot, bool isDrink)
	{
		if (Inventory[fromSlot] == null)
		{
			//Already been eaten or the food is no longer in hand
			return;
		}

		FoodBehaviour baseFood = food.GetComponent<FoodBehaviour>();
		if (isDrink)
		{
			soundNetworkActions.CmdPlaySoundAtPlayerPos("Slurp");
		}
		else
		{
			soundNetworkActions.CmdPlaySoundAtPlayerPos("EatFood");
		}
		PlayerHealth playerHealth = GetComponent<PlayerHealth>();

		//FIXME: remove health and blood changes after TDM
		//and use this Cmd for healing hunger and applying 
		//food related attributes instead:
		playerHealth.AddHealth(baseFood.healAmount);
		playerHealth.BloodLevel += baseFood.healAmount;
		playerHealth.StopBleeding();

		PoolManager.Instance.PoolNetworkDestroy(food);
		UpdateSlotMessage.Send(gameObject, fromSlot, null, true);
		Inventory[fromSlot] = null;
		equipment.ClearItemSprite(fromSlot);
	}

	[Command]
	public void CmdSetActiveHand(string hand)
	{
		activeHand = hand;
	}

	[Command]
	public void CmdRefillWelder(GameObject welder, GameObject weldingTank)
	{
		//Double check reach just in case:
		if (playerScript.IsInReach(weldingTank))
		{
			var w = welder.GetComponent<Welder>();

			//is the welder on?
			if (w.isOn)
			{
				weldingTank.GetComponent<ExplodeWhenShot>().ExplodeOnDamage(gameObject.name);
			}
			else
			{
				//Refuel!
				w.Refuel();
				RpcPlayerSoundAtPos("Refill", transform.position, true);
			}
		}
	}

	[Command]
	public void CmdRequestPaperEdit(GameObject paper, string newMsg)
	{
		//Validate paper edit request
		//TODO Check for Pen
		if (Inventory["leftHand"].Item == paper || Inventory["rightHand"].Item == paper)
		{
			var paperComponent = paper.GetComponent<Paper>();
			var pen = Inventory["leftHand"].Item?.GetComponent<Pen>();
			if (pen == null)
			{
				pen = Inventory["rightHand"].Item?.GetComponent<Pen>();
				if (pen == null)
				{
					//no pen
					paperComponent.UpdatePlayer(gameObject); //force server string to player
					return;
				}
			}

			if (paperComponent != null)
			{
				paperComponent.SetServerString(newMsg);
				paperComponent.UpdatePlayer(gameObject);
			}
		}
	}
}