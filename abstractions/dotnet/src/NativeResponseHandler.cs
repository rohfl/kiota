using System.Threading.Tasks;

namespace Kiota.Abstractions {
    public class NativeResponseHandler : IResponseHandler {
        public object Value;
        public Task<ModelType> HandleResponseAsync<NativeResponseType, ModelType>(NativeResponseType response) {
            Value = response;
            return Task.FromResult(default(ModelType));
        }
    }
}
