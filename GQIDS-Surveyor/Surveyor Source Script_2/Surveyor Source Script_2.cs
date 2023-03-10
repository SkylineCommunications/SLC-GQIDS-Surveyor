using System;
using System.Collections.Generic;
using System.Linq;
using Skyline.DataMiner.Analytics.GenericInterface;
using Skyline.DataMiner.Net;
using Skyline.DataMiner.Net.Enums;
using Skyline.DataMiner.Net.Messages;

[GQIMetaData(Name = "Surveyor")]
public class SurveyorSource : IGQIDataSource, IGQIOnInit, IGQIInputArguments
{
	private readonly GQIStringColumn _idColumn;
	private readonly GQIStringColumn _typeColumn;
	private readonly GQIStringColumn _nameColumn;
	private readonly GQIStringColumn _severityColumn;
	private readonly GQIStringColumn _keyColumn;

	private readonly GQIStringArgument _filterArg;

	private GQIDMS _dms;
	private string _filter;

	public SurveyorSource()
	{
		_idColumn = new GQIStringColumn("Id");
		_typeColumn = new GQIStringColumn("Type");
		_nameColumn = new GQIStringColumn("Name");
		_severityColumn = new GQIStringColumn("Severity");
		_keyColumn = new GQIStringColumn("Key");

		_filterArg = new GQIStringArgument("Filter")
		{
			IsRequired = false,
		};
	}

	public OnInitOutputArgs OnInit(OnInitInputArgs args)
	{
		_dms = args.DMS;
		return default;
	}

	public GQIArgument[] GetInputArguments()
	{
		return new[] { _filterArg };
	}

	public OnArgumentsProcessedOutputArgs OnArgumentsProcessed(OnArgumentsProcessedInputArgs args)
	{
		_filter = args.GetArgumentValue(_filterArg);
		return default;
	}

	public GQIColumn[] GetColumns()
	{
		return new GQIColumn[]
		{
			_idColumn,
			_typeColumn,
			_nameColumn,
			_severityColumn,
			_keyColumn
		};
	}

	public GQIPage GetNextPage(GetNextPageInputArgs args)
	{
		var rows = GetRows(_filter);
		return new GQIPage(rows.ToArray());
	}

	private IEnumerable<GQIRow> GetRows(string filter)
	{
		if (string.IsNullOrEmpty(filter))
			return GetRowsForView(-1);

		var parts = _filter.Split(':');
		switch(parts[0])
		{
			case "View":
				var viewID = int.Parse(parts[1]);
				return GetRowsForView(viewID);
			case "Service":
				var serviceID = ServiceID.FromString(parts[1]);
				return GetRowsForService(serviceID.DataMinerID, serviceID.SID);
			case "Element":
				{
					var elementID = ElementID.FromString(parts[1]);
					return GetRowsForElement(elementID.DataMinerID, elementID.EID);
				}
			case "Parameter":
				{
					var ids = parts[1].Split('/');
					var dmaID = int.Parse(ids[0]);
					var elementID = int.Parse(ids[1]);
					return GetRowsForElement(dmaID, elementID);
				}
		}
		throw new Exception("Unknown filter");
	}

	private IEnumerable<GQIRow> GetRowsForView(int viewID)
	{
		var rows = new List<GQIRow>();
		var views = GetViews();

		var view = views[viewID];
		var parentView = views[view.ParentId];
		rows.Add(CreateViewRow(parentView));
		rows.Add(CreateSeparatorRow());

		rows.AddRange(GetViewsForView(view, views));
		rows.AddRange(GetElementsForView(viewID));
		rows.AddRange(GetServicesForView(viewID));
		return rows;
	}

	private Dictionary<int, ViewInfoEventMessage> GetViews()
	{
		var views = new Dictionary<int, ViewInfoEventMessage>();
		var request = new GetInfoMessage(InfoType.ViewInfo);
		var responses = _dms.SendMessages(request);
		foreach (ViewInfoEventMessage view in responses)
			views[view.ID] = view;
		return views;
	}

	private IEnumerable<GQIRow> GetRowsForService(int dmaID, int serviceID)
	{
		var rows = new List<GQIRow>();

		var views = GetViewsForService(dmaID, serviceID)
			.Select(CreateViewRow);
		rows.AddRange(views);
		rows.Add(CreateSeparatorRow());

		var service = GetService(dmaID, serviceID);
		var children = service.ServiceParams
			.Where(child => !child.IsExcluded)
			.Select(CreateServiceChildRow);
		rows.AddRange(children);

		return rows;
	}

	private IEnumerable<GQIRow> GetRowsForElement(int dmaID, int elementID)
	{
		var rows = new List<GQIRow>();

		var views = GetViewsForElement(dmaID, elementID)
			.Select(CreateViewRow);
		rows.AddRange(views);
		rows.Add(CreateSeparatorRow());

		var protocol = GetProtocolForElement(dmaID, elementID);
		rows.AddRange(GetParametersForProtocol(protocol));

		return rows;
	}

	private IEnumerable<GQIRow> GetParametersForProtocol(GetElementProtocolResponseMessage protocol)
	{
		return protocol.Parameters
			.Where(IncludeParameter)
			.Select(parameter => CreateParameterRow(protocol.DataMinerID, protocol.ElementID, parameter));
	}

	private bool IncludeParameter(ParameterInfo parameter)
	{
		if (parameter.WriteType)
			return false;
		if (parameter.IsTable)
			return false;
		if (parameter.IsTableColumn)
			return false;
		if (!parameter.HasDisplayPositions)
			return false;
		if (!parameter.IsAverageTrended())
			return false;

		return true;
	}

	private ServiceInfoEventMessage GetService(int dmaID, int serviceID)
	{
		var request = new GetServiceByIDMessage(dmaID, serviceID);
		return _dms.SendMessage(request) as ServiceInfoEventMessage;
	}

	private LiteServiceInfoEvent GetLiteService(int dmaID, int serviceID)
	{
		var request = GetLiteServiceInfo.ByID(dmaID, serviceID);
		return _dms.SendMessage(request) as LiteServiceInfoEvent;
	}

	private LiteElementInfoEvent GetLiteElement(int dmaID, int elementID)
	{
		var request = GetLiteElementInfo.ByID(dmaID, elementID);
		return _dms.SendMessage(request) as LiteElementInfoEvent;
	}

	private IEnumerable<ViewInfoEventMessage> GetViewsForService(int dmaID, int serviceID)
	{
		var request = new GetViewsForServiceMessage(dmaID, serviceID);
		var response = _dms.SendMessage(request) as GetViewsForServiceResponse;

		var views = GetViews();
		return response.Views.Select(view => views[view.ID]);
	}

	private IEnumerable<ViewInfoEventMessage> GetViewsForElement(int dmaID, int elementID)
	{
		var request = new GetViewsForElementMessage(dmaID, elementID);
		var response = _dms.SendMessage(request) as GetViewsForElementResponse;

		var views = GetViews();
		return response.Views.Select(view => views[view.ID]);
	}

	private GetElementProtocolResponseMessage GetProtocolForElement(int dmaID, int elementID)
	{
		var request = new GetElementProtocolMessage(dmaID, elementID);
		return _dms.SendMessage(request) as GetElementProtocolResponseMessage;
	}

	private IEnumerable<GQIRow> GetViewsForView(ViewInfoEventMessage view, Dictionary<int, ViewInfoEventMessage> views)
	{
		if (view.DirectChildViews is null)
			return Enumerable.Empty<GQIRow>();

		return view.DirectChildViews
			.Select(childViewID => views[childViewID])
			.OrderBy(childView => childView.Name)
			.Select(CreateViewRow);
	}

	private GQIRow CreateViewRow(ViewInfoEventMessage view)
	{
		var id = $"{view.ID}";
		var type = "View";
		var name = view.Name;
		var severity = GetViewSeverity(view.ID);
		return CreateRow(id, type, name, severity, $"{type}:{id}");
	}

	private AlarmLevel GetViewSeverity(int viewID)
	{
		var request = new GetViewStateMessage { ViewID = viewID };
		var response = _dms.SendMessage(request) as GetViewStateResponse;
		return response.States[0].Level;
	}

	private IEnumerable<GQIRow> GetServicesForView(int viewID)
	{
		var request = new GetLiteServiceInfo
		{
			ExcludeSubViews = true,
			ViewID = viewID,
		};
		var responses = _dms.SendMessages(request);
		return responses
			.OfType<LiteServiceInfoEvent>()
			.OrderBy(service => service.Name)
			.Select(CreateServiceRow);
	}

	private GQIRow CreateServiceRow(LiteServiceInfoEvent service)
	{
		var id = ElementID.GetKey(service.DataMinerID, service.ElementID);
		var type = "Service";
		var name = service.Name;
		var severity = GetServiceSeverity(service.DataMinerID, service.ElementID);
		return CreateRow(id, type, name, severity, $"{type}:{id}");
	}

	private GQIRow CreateServiceChildRow(ServiceInfoParams child)
	{
		if (child.IsService)
		{
			var service = GetLiteService(child.DataMinerID, child.ElementID);
			return CreateServiceRow(service);
		}

		var element = GetLiteElement(child.DataMinerID, child.ElementID);
		return CreateElementRow(element);
	}

	private AlarmLevel GetServiceSeverity(int dmaID, int serviceID)
	{
		var request = new GetServiceStateMessage
		{
			DataMinerID = dmaID,
			ServiceID = serviceID
		};
		var response = _dms.SendMessage(request) as ServiceStateEventMessage;
		return response.Level;
	}

	private IEnumerable<GQIRow> GetElementsForView(int viewID)
	{
		var request = new GetLiteElementInfo()
		{
			ExcludeSubViews = true,
			ViewID = viewID,
			IncludeHidden = false,
			IncludePaused = true,
			IncludeStopped = true
		};
		var responses = _dms.SendMessages(request);
		return responses
			.OfType<LiteElementInfoEvent>()
			.OrderBy(element => element.Name)
			.Select(CreateElementRow);
	}

	private GQIRow CreateElementRow(LiteElementInfoEvent element)
	{
		var id = ElementID.GetKey(element.DataMinerID, element.ElementID);
		var type = "Element";
		var name = element.Name;
		var severity = GetElementSeverity(element.DataMinerID, element.ElementID);
		return CreateRow(id, type, name, severity, $"{type}:{id}");
	}

	private AlarmLevel GetElementSeverity(int dmaID, int elementID)
	{
		var request = new GetAlarmStateMessage(dmaID, elementID);
		var response = _dms.SendMessage(request) as GetAlarmStateResponseMessage;
		return response.Level;
	}

	private GQIRow CreateParameterRow(int dmaID, int elementID, ParameterInfo parameter)
	{
		var id = $"{dmaID}/{elementID}/{parameter.ID}";
		var type = "Parameter";
		var name = parameter.Name;
		var severity = GetParameterSeverity(dmaID, elementID, parameter.ID);
		return CreateRow(id, type, name, severity, $"{type}:{id}");
	}

	private AlarmLevel GetParameterSeverity(int dmaID, int elementID, int parameterID)
	{
		var request = new GetParameterStateMessage
		{
			DataMinerID = dmaID,
			ElementID = elementID,
			ParameterID = parameterID
		};
		var response = _dms.SendMessage(request) as ParameterStateEventMessage;
		var severity = (EnumSeverity)response.SeverityID;
		return FromSeverity(severity);
	}

	private AlarmLevel FromSeverity(EnumSeverity severity)
	{
		switch(severity)
		{
			case EnumSeverity.Error: return AlarmLevel.Error;
			case EnumSeverity.Timeout: return AlarmLevel.Timeout;
			case EnumSeverity.Critical: return AlarmLevel.Critical;
			case EnumSeverity.Major: return AlarmLevel.Major;
			case EnumSeverity.Minor: return AlarmLevel.Minor;
			case EnumSeverity.Warning: return AlarmLevel.Warning;
			case EnumSeverity.Normal: return AlarmLevel.Normal;
			case EnumSeverity.Notice: return AlarmLevel.Notice;
			case EnumSeverity.Information: return AlarmLevel.Information;
			case EnumSeverity.Suggestion: return AlarmLevel.Suggestion;
		}
		return AlarmLevel.Undefined;
	}

	private GQIRow CreateRow(string id, string type, string name, AlarmLevel severity, string key)
	{
		var cells = new[]
		{
			new GQICell { Value = id },
			new GQICell { Value = type },
			new GQICell { Value = name },
			new GQICell { Value = $"{severity}" },
			new GQICell { Value = key }
		};
		return new GQIRow(cells);
	}

	private GQIRow CreateSeparatorRow()
	{
		string id = null;
		string type = "Separator";
		string name = null;
		var severity = AlarmLevel.Undefined;
		string key = _filter;
		return CreateRow(id, type, name, severity, key);
	}
}