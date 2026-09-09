using System.Collections.Generic;

namespace S7CommPlusDriver.Internal
{
    internal sealed class S7CommPlusProtocolRequests
    {
        private readonly IS7CommPlusProtocolSession _session;

        public S7CommPlusProtocolRequests(IS7CommPlusProtocolSession session)
        {
            _session = session;
        }

        public int Explore(
            uint relationId,
            IEnumerable<uint> attributes,
            out ExploreResponse response,
            byte exploreChildsRecursive = 1,
            byte exploreParents = 0)
        {
            response = null;

            var request = new ExploreRequest(ProtocolVersion.V2)
            {
                ExploreId = relationId,
                ExploreRequestId = Ids.None,
                ExploreChildsRecursive = exploreChildsRecursive,
                ExploreParents = exploreParents
            };

            if (attributes != null)
            {
                request.AddressList.AddRange(attributes);
            }

            return SendExplore(request, out response);
        }

        public int SendExplore(ExploreRequest request, out ExploreResponse response)
        {
            response = null;
            var result = SendAndReceive(request);
            if (result != 0)
            {
                return result;
            }

            response = ExploreResponse.DeserializeFromPdu(_session.ReceivedPdu, true);
            return _session.CheckResponse(request, response);
        }

        public int GetVarSubstreamed(uint objectId, ushort address, out PValue value)
        {
            value = null;

            var request = new GetVarSubstreamedRequest(ProtocolVersion.V2)
            {
                InObjectId = objectId,
                SessionId = _session.SessionId,
                Address = address
            };

            var result = SendAndReceive(request);
            if (result != 0)
            {
                return result;
            }

            var response = GetVarSubstreamedResponse.DeserializeFromPdu(_session.ReceivedPdu);
            if (response == null)
            {
                return S7Consts.errIsoInvalidPDU8;
            }

            value = response.Value;
            return 0;
        }

        public int GetVariable(uint objectId, uint address, out PValue value)
        {
            value = null;

            var request = new GetVariableRequest(ProtocolVersion.V2)
            {
                InObjectId = objectId,
                SessionId = _session.SessionId,
                Address = address
            };

            var result = SendAndReceive(request);
            if (result != 0)
            {
                return result;
            }

            var response = GetVariableResponse.DeserializeFromPdu(_session.ReceivedPdu);
            if (response == null)
            {
                return S7Consts.errIsoInvalidPDU8;
            }

            if (response.ReturnValue != 0 || response.Value == null)
            {
                return S7Consts.errIsoInvalidPDU8;
            }

            value = response.Value;
            return 0;
        }

        public int CreateObject(CreateObjectRequest request, out CreateObjectResponse response)
        {
            response = null;
            var result = SendAndReceive(request);
            if (result != 0)
            {
                return result;
            }

            response = CreateObjectResponse.DeserializeFromPdu(_session.ReceivedPdu);
            return response == null ? S7Consts.errIsoInvalidPDU : 0;
        }

        public int WaitNotification(int timeoutMilliseconds, out Notification notification)
        {
            return WaitNotification(0, timeoutMilliseconds, out notification);
        }

        public int WaitNotification(uint subscriptionObjectId, int timeoutMilliseconds, out Notification notification)
        {
            notification = null;
            var result = _session.WaitForNotification(subscriptionObjectId, timeoutMilliseconds, out notification);
            if (result != 0)
            {
                return result;
            }

            return notification == null ? S7Consts.errIsoInvalidPDU : 0;
        }

        public int SetSubscriptionCreditLimit(uint subscriptionObjectId, short limit)
        {
            if (subscriptionObjectId == 0)
            {
                return S7Consts.errCliInvalidParams;
            }

            var request = new SetVariableRequest(ProtocolVersion.V2)
            {
                TransportFlags = S7CommPlusProtocolConstants.FireAndForgetTransportFlags,
                InObjectId = subscriptionObjectId,
                Address = Ids.SubscriptionCreditLimit,
                Value = new ValueInt(limit)
            };

            return _session.SendFunction(request);
        }

        public int SetVariable(uint objectId, uint address, PValue value)
        {
            if (objectId == 0 || value == null)
            {
                return S7Consts.errCliInvalidParams;
            }

            var request = new SetVariableRequest(ProtocolVersion.V2)
            {
                TransportFlags = S7CommPlusProtocolConstants.FireAndForgetTransportFlags,
                InObjectId = objectId,
                Address = address,
                Value = value
            };

            return _session.SendFunction(request);
        }

        public int SetVariableAcknowledged(uint objectId, uint address, PValue value)
        {
            if (objectId == 0 || value == null)
            {
                return S7Consts.errCliInvalidParams;
            }

            var request = new SetVariableRequest(ProtocolVersion.V2)
            {
                TransportFlags = S7CommPlusProtocolConstants.RequestWithResponseTransportFlags,
                InObjectId = objectId,
                Address = address,
                Value = value
            };

            var result = SendAndReceive(request);
            if (result != 0)
            {
                return result;
            }

            var response = SetVariableResponse.DeserializeFromPdu(_session.ReceivedPdu);
            if (response == null)
            {
                return S7Consts.errIsoInvalidPDU;
            }

            result = _session.CheckResponse(request, response);
            if (result != 0)
            {
                return result;
            }

            return response.ReturnValue == 0
                ? 0
                : S7Consts.errCliFunctionRefused;
        }

        public int SetMultiVariablesRaw(uint inObjectId, IEnumerable<uint> addressFields, IEnumerable<PValue> values)
        {
            if (addressFields == null || values == null)
            {
                return S7Consts.errCliInvalidParams;
            }

            var request = new SetMultiVariablesRequest(ProtocolVersion.V2)
            {
                InObjectId = inObjectId,
                UseRawAddressList = true
            };
            request.AddressList.AddRange(addressFields);
            request.ValueList.AddRange(values);
            if (request.ValueList.Count == 0)
            {
                return S7Consts.errCliInvalidParams;
            }

            var result = SendAndReceive(request);
            if (result != 0)
            {
                return result;
            }

            var response = SetMultiVariablesResponse.DeserializeFromPdu(_session.ReceivedPdu);
            if (response == null)
                return S7Consts.errIsoInvalidPDU;
            var resultError = _session.CheckResponse(request, response);
            if (resultError != 0)
                return resultError;
            if (response.ReturnValue != 0)
                return S7Consts.errCliFunctionRefused;
            foreach (var item in response.ErrorValues)
            {
                if (item.Value != 0)
                    return S7Consts.errCliFunctionRefused;
            }
            return 0;
        }

        private int SendAndReceive(IS7pRequest request)
        {
            return _session.SendFunctionAndWait(request);
        }
    }
}
