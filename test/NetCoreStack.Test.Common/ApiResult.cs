using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Internal;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections;
using System.Reflection;
using System.Threading.Tasks;

namespace NetCoreStack.Test.Common
{
    public abstract class BaseApiResult : ActionResult
    {
        private readonly Type _resultType;

        public string Message { get; set; }

        public int StatusCode { get; set; }

        public string Metadata { get; set; }

        public string MetadataInner { get; set; }

        protected abstract object GetResultOrValue { get; }

        public BaseApiResult(Type resultType, string message = "", int statusCode = StatusCodes.Status200OK)
        {
            _resultType = resultType;
            Message = message;
            StatusCode = statusCode;
        }

        protected void SetMetadata(object instance)
        {
            if (_resultType == null)
                return;

            if (_resultType == typeof(string))
            {
                Metadata = typeof(string).Name;
                return;
            }

            var typeName = _resultType.FullName;
            if (typeName.Contains("AnonymousType"))
                Metadata = typeof(object).FullName;
            else
            {
                Metadata = typeName;
                if (typeof(IEnumerable).IsAssignableFrom(_resultType))
                {
                    Metadata = nameof(IEnumerable);
                    var underlyingTypes = _resultType.GetGenericArguments();
                    if (underlyingTypes.Length > 0)
                        MetadataInner = _resultType.GetGenericArguments()[0].FullName;
                }
            }
        }

        public override Task ExecuteResultAsync(ActionContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var response = context.HttpContext.Response;
            response.StatusCode = StatusCode;
            var executor = context.HttpContext.RequestServices.GetRequiredService<ObjectResultExecutor>();
            var result = executor.ExecuteAsync(context, new ObjectResult(this));
            return result;
        }
    }

    public class ApiResult : BaseApiResult
    {
        public object Value { get; set; }

        public ApiResult(object value = null, string message = "", int statusCode = StatusCodes.Status200OK)
            : base(value != null ? value.GetType() : null, message, statusCode)
        {
            Value = value;
            SetMetadata(value);
        }

        protected override object GetResultOrValue
        {
            get
            {
                return Value;
            }
        }
    }

    public sealed class ApiResult<T> : BaseApiResult
    {
        public T Result { get; set; }

        public ApiResult(T result = default(T), string message = "", int statusCode = StatusCodes.Status200OK)
            : base(typeof(T), message, statusCode)
        {
            Result = result;
            SetMetadata(Result);
        }

        protected override object GetResultOrValue
        {
            get
            {
                return Result;
            }
        }
    }
}
