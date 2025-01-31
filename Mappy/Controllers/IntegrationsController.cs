﻿using System;
using System.Linq;
using System.Numerics;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using ImGuiNET;
using KamiLib.Classes;
using Lumina.Excel.GeneratedSheets;
using Mappy.Classes;
using MapType = FFXIVClientStructs.FFXIV.Client.UI.Agent.MapType;
using SeString = Lumina.Text.SeString;

namespace Mappy.Controllers;

public unsafe class IntegrationsController : IDisposable {
	private delegate void OpenMapByMapIdDelegate(AgentMap* thisPtr, uint mapId, uint a3, bool a4);
    
	private readonly Hook<AgentMap.Delegates.ShowMap>? showMapHook;
	private readonly Hook<OpenMapByMapIdDelegate>? openMapByIdHook;
	private readonly Hook<AgentMap.Delegates.OpenMap>? openMapHook;
    
	public IntegrationsController() {
		showMapHook ??= Service.Hooker.HookFromAddress<AgentMap.Delegates.ShowMap>(AgentMap.MemberFunctionPointers.ShowMap, OnShowHook);
		openMapByIdHook ??= Service.Hooker.HookFromAddress<OpenMapByMapIdDelegate>(AgentMap.MemberFunctionPointers.OpenMapByMapId, OpenMapById);
		openMapHook ??= Service.Hooker.HookFromAddress<AgentMap.Delegates.OpenMap>(AgentMap.MemberFunctionPointers.OpenMap, OpenMap);
        
		if (Service.ClientState is { IsPvP: false }) {
			EnableIntegrations();
            
			if (AgentMap.Instance()->AgentInterface.AddonId is not 0) {
				TryYeetMap();
				System.MapWindow.Open();
			}
		}
        
		Service.ClientState.EnterPvP += DisableIntegrations;
		Service.ClientState.LeavePvP += EnableIntegrations;
	}

	public void Dispose() {
		DisableIntegrations();
        
		showMapHook?.Dispose();
		openMapByIdHook?.Dispose();
		openMapHook?.Dispose();
        
		TryUnYeetMap();
        
		Service.ClientState.EnterPvP -= DisableIntegrations;
		Service.ClientState.LeavePvP -= EnableIntegrations;
	}

	private void EnableIntegrations() {
		Service.Log.Debug("Enabling Integrations");
        
		showMapHook?.Enable();
		openMapByIdHook?.Enable();
		openMapHook?.Enable();
	}

	private void DisableIntegrations() {
		Service.Log.Debug("Disabling Integrations");
        
		showMapHook?.Disable();
		openMapByIdHook?.Disable();
		openMapHook?.Disable();
	}

	private void OnShowHook(AgentMap* agent, bool a1, bool a2)
		=> HookSafety.ExecuteSafe(() => {
			if (AgentMap.Instance()->AddonId is not 0 && AgentMap.Instance()->EventMarkers.Count is 0) {
				System.MapWindow.Close();
				TryUnYeetMap();
				return;
			}
            
			showMapHook!.Original(agent, a1, a2);
			TryMirrorGameState(agent);

			Service.Log.Debug($"Show Map: {a1}, {a2}");
		}, Service.Log, "Exception during OnShowHook");

	private void OpenMapById(AgentMap* agent, uint mapId, uint a3, bool a4) 
		=> HookSafety.ExecuteSafe(() => {
			openMapByIdHook!.Original(agent, mapId, a3, a4);
			TryMirrorGameState(agent);
            
			Service.Log.Debug($"Open Map By ID: {mapId}, {a3}, {a4}");
		}, Service.Log, "Exception during OpenMapByMapId");
    
	private void OpenMap(AgentMap* agent, OpenMapInfo* mapInfo) 
		=> HookSafety.ExecuteSafe(() => {
			openMapHook!.Original(agent, mapInfo);
			TryMirrorGameState(agent);

			switch (mapInfo->Type) {
				case MapType.QuestLog: {
					if (GetMapIdForQuest(mapInfo) is {} targetMapId ) {
						OpenMap(targetMapId);
					}
                    
					CenterOnMarker(agent, agent->TempMapMarkers[0].MapMarker);
					break;
				}

				case MapType.GatheringLog: {
					CenterOnMarker(agent, agent->TempMapMarkers[0].MapMarker);
					break;
				}

				case MapType.FlagMarker: {
					CenterOnMarker(agent, agent->FlagMapMarker.MapMarker);
					break;
				}
			}
            
			Service.Log.Debug($"Open Map With OpenMapInfo: {mapInfo->MapId}, {mapInfo->Type}, {mapInfo->TerritoryId}, {mapInfo->TitleString}");
		}, Service.Log, "Exception during OpenMap");

	public void OpenMap(uint mapId)
		=> AgentMap.Instance()->OpenMapByMapId(mapId, 0, true);

	public void OpenOccupiedMap()
		=> OpenMap(AgentMap.Instance()->CurrentMapId);

	private static void CenterOnMarker(AgentMap* agent, MapMarkerBase marker) {
		var coordinates = new Vector2(marker.X, marker.Y) / 16.0f * agent->SelectedMapSizeFactorFloat;

		System.MapWindow.ProcessingCommand = true;
		System.MapRenderer.DrawOffset = -coordinates;
	}

	private uint? GetMapIdForQuest(OpenMapInfo* mapInfo) {
		foreach (var leveQuest in QuestManager.Instance()->LeveQuests) {
			if (leveQuest.IsHidden || leveQuest.LeveId is 0) continue;

			var leveData = Service.DataManager.GetExcelSheet<Leve>()?.GetRow(leveQuest.LeveId)!;
			if (!IsNameMatch(leveData.Name, mapInfo)) continue;

			return leveData.LevelStart.Value?.Map.Row;
		}
		
		foreach (var quest in QuestManager.Instance()->NormalQuests) {
			
			// If quest is hidden, or id is invalid, skip. todo: consider if IsHidden is a good idea
			if (quest.IsHidden || quest.QuestId is 0) continue;
			
			// Is this the quest we are looking for?
			var questData = Service.DataManager.GetExcelSheet<CustomQuestSheet>()?.GetRow(quest.QuestId + 65536u)!;
			if (!IsNameMatch(questData.Name, mapInfo)) continue;

			var todoPrimaryIndex = 0;
			if (quest.Sequence is 0xFF) {
				// For each of the possible steps check if out sequence matches that index
				foreach(var index in Enumerable.Range(0, 24)) {
					if (questData.ToDoCompleteSeq[index] == quest.Sequence) {
						todoPrimaryIndex = index;
						break;
					}
				}
			}
			else {
				todoPrimaryIndex = quest.Sequence;
			}
			
			// Iterate the level data for markers for the current sequence number
			foreach (var index in Enumerable.Range(0, 8)) {
				var levelData = questData.ToDoLocation[todoPrimaryIndex, index];
				if (levelData.Row is 0) continue;
				if (levelData.Value is null) continue;

				return levelData.Value.Map.Row;
			}
		}

		return Service.DataManager.GetExcelSheet<Quest>()?.FirstOrDefault(quest =>
			IsNameMatch(quest.Name, mapInfo) &&
			quest is { IssuerLocation.Row: not 0 })
			?.IssuerLocation.Value?.Map.Row;
	}

	private bool IsNameMatch(SeString name, OpenMapInfo* mapInfo) 
		=> string.Equals(name.ToString(), mapInfo->TitleString.ToString(), StringComparison.OrdinalIgnoreCase);
	
	private void TryMirrorGameState(AgentMap* agent) {
		if (agent->AddonId is not 0) {
			System.MapWindow.Open();
			ImGui.SetWindowFocus(System.MapWindow.WindowName);
			TryYeetMap();
		}
		else {
			System.MapWindow.Close();
			TryUnYeetMap();
		}
	}
    
	public void TryYeetMap() {
		var areaMap = (AtkUnitBase*)Service.GameGui.GetAddonByName("AreaMap");
		if (areaMap is not null && areaMap->RootNode is not null) {
			areaMap->RootNode->SetPositionFloat(-9000.1f, -9000.1f);
		}
	}

	public void TryUnYeetMap() {
		var areaMap = (AtkUnitBase*)Service.GameGui.GetAddonByName("AreaMap");
		if (areaMap is not null && areaMap->RootNode is not null) {
			areaMap->Close(true);
            
			// Little hacky, but we need to wait just a little bit before setting the position, or else we'll see the vanilla map flash closed really quick.
			Service.Framework.RunOnTick(() => areaMap->RootNode->SetPositionFloat(areaMap->X, areaMap->Y), delayTicks: 10);
		}
	}
}