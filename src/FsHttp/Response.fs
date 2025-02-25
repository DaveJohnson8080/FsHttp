module FsHttp.Response

open System
open System.IO
open System.Net
open System.Text
open System.Text.Json
open System.Xml.Linq

open FsHttp
open FsHttp.GlobalConfig.Json
open FsHttp.Helper


// -----------
// Content transformation
// -----------

// TODO: For all functions that serialize response content, it should be checked that content-length
//       header is set, and if not, a "maxSerContentLength" should be used or an exception thrown.

// TODO: Cancellation token passing in all content based async method calls

/// Starts serializing the content stream into a buffer as a background task.
let loadContent (response: Response) =
    response.content.LoadIntoBufferAsync() |> ignore
    response

let toStreamTAsync response = response.content.ReadAsStreamAsync()
let toStreamAsync response = toStreamTAsync response |> Async.AwaitTask
let toStream response = (toStreamTAsync response).Result

let parseAsync parserName parse response =
    async {
        use! contentStream = toStreamAsync response
        use bufferingStream = new Stream.Utf8StringBufferingStream(contentStream, None)

        try
            let! ct = Async.CancellationToken
            return! parse (bufferingStream :> Stream) ct
        with ex ->
            let errorDisplayContent = bufferingStream.GetUtf8String()

            let msg =
                $"Could not parse %s{parserName}: {ex.Message}{Environment.NewLine}Content:{Environment.NewLine}{errorDisplayContent}"

            return raise (Exception(msg, ex))
    }

let toBytesAsync response =
    async {
        let! stream = response |> toStreamAsync
        return! stream |> Stream.toBytesAsync
    }

let toBytesTAsync response = toBytesAsync response |> Async.StartAsTask
let toBytes response = toBytesAsync response |> Async.RunSynchronously

let private toStringWithLengthAsync maxLength response =
    async {
        use! stream = response |> toStreamAsync

        match maxLength with
        | None ->
            use sr = new StreamReader(stream)
            return! sr.ReadToEndAsync() |> Async.AwaitTask
        | Some maxLength -> return! stream |> Stream.readUtf8StringAsync maxLength
    }

let toStringAsync maxLength response = toStringWithLengthAsync maxLength response
let toStringTAsync maxLength response = toStringAsync maxLength response |> Async.StartAsTask
let toString maxLength response = toStringAsync maxLength response |> Async.RunSynchronously

let toTextAsync response = toStringWithLengthAsync None response
let toTextTAsync response = toTextAsync response |> Async.StartAsTask
let toText response = toTextAsync response |> Async.RunSynchronously

#if NETSTANDARD2_0
let toXmlAsync response =
    response
    |> parseAsync "XML" (fun stream ct -> async { return XDocument.Load(stream, LoadOptions.SetLineInfo) })
#else
let toXmlAsync response =
    response
    |> parseAsync "XML" (fun stream ct -> XDocument.LoadAsync(stream, LoadOptions.SetLineInfo, ct) |> Async.AwaitTask)
#endif
let toXmlTAsync response = toXmlAsync response |> Async.StartAsTask
let toXml response = toXmlAsync response |> Async.RunSynchronously

// TODO: toHtml


// -----------
// JSON (System.Text.Json)
//      It is intended to shadow (some) of the provided JSON functions and operators
//      in additional JSON integration packages.
// -----------

let toJsonDocumentWithAsync options response =
    response
    |> parseAsync
        "JSON"
        (fun stream ct ->
            JsonDocument.ParseAsync(stream, options, cancellationToken = ct)
            |> Async.AwaitTask
        )

let toJsonDocumentWithTAsync options response = toJsonDocumentWithAsync options response |> Async.StartAsTask
let toJsonDocumentWith options response = toJsonDocumentWithAsync options response |> Async.RunSynchronously

let toJsonDocumentAsync response = toJsonDocumentWithAsync defaultJsonDocumentOptions response
let toJsonDocumentTAsync response = toJsonDocumentWithTAsync defaultJsonDocumentOptions response
let toJsonDocument response = toJsonDocumentWith defaultJsonDocumentOptions response

let toJsonWithAsync options response =
    toJsonDocumentWithAsync options response
    |> Async.map (fun doc -> doc.RootElement)

let toJsonWithTAsync options response = toJsonWithAsync options response |> Async.StartAsTask
let toJsonWith options response = toJsonWithAsync options response |> Async.RunSynchronously

let toJsonAsync response = toJsonWithAsync defaultJsonDocumentOptions response
let toJsonTAsync response = toJsonWithTAsync defaultJsonDocumentOptions response
let toJson response = toJsonWith defaultJsonDocumentOptions response

let toJsonSeqWithAsync options response =
    toJsonWithAsync options response
    |> Async.map (fun json -> json.EnumerateArray())

let toJsonSeqWithTAsync options response = toJsonSeqWithAsync options response |> Async.StartAsTask
let toJsonSeqWith options response = toJsonSeqWithAsync options response |> Async.RunSynchronously

let toJsonSeqAsync response = toJsonSeqWithAsync defaultJsonDocumentOptions response
let toJsonSeqTAsync response = toJsonSeqWithTAsync defaultJsonDocumentOptions response
let toJsonSeq response = toJsonSeqWith defaultJsonDocumentOptions response

let toJsonArrayWithAsync options response = toJsonSeqWithAsync options response |> Async.map Seq.toArray
let toJsonArrayWithTAsync options response = toJsonArrayWithAsync options response |> Async.StartAsTask
let toJsonArrayWith options response = toJsonArrayWithAsync options response |> Async.RunSynchronously

let toJsonArrayAsync response = toJsonArrayWithAsync defaultJsonDocumentOptions response
let toJsonArrayTAsync response = toJsonArrayWithTAsync defaultJsonDocumentOptions response
let toJsonArray response = toJsonArrayWith defaultJsonDocumentOptions response

let deserializeJsonWithAsync<'a> options response =
    async {
        use! stream = toStreamAsync response
        let! ct = Async.CancellationToken

        return!
            JsonSerializer
                .DeserializeAsync<'a>(stream, options = options, cancellationToken = ct)
                .AsTask()
            |> Async.AwaitTask

    }

let deserializeJsonWithTAsync<'a> options response = deserializeJsonWithAsync<'a> options response |> Async.StartAsTask
let deserializeJsonWith<'a> options response = deserializeJsonWithAsync<'a> options response |> Async.RunSynchronously

let deserializeJsonAsync<'a> response = deserializeJsonWithAsync<'a> defaultJsonSerializerOptions response
let deserializeJsonTAsync<'a> response = deserializeJsonWithTAsync<'a> defaultJsonSerializerOptions response
let deserializeJson<'a> response = deserializeJsonWith<'a> defaultJsonSerializerOptions response


// -----------
// Formatting
// -----------

/// Tries to convert the response content according to it's type to a formatted string.
let toFormattedTextAsync response =
    let toJsonString (doc: JsonDocument) =
        use stream = new MemoryStream()
        let writer = new Utf8JsonWriter(stream, new JsonWriterOptions(Indented = true))
        do doc.WriteTo(writer)
        do writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())

    async {
        let mediaType =
            try
                response.content.Headers.ContentType.MediaType
            with _ ->
                ""

        try
            if mediaType.Contains("/json") then
                let! json = toJsonDocumentAsync response
                return toJsonString json
            else if mediaType.Contains("/xml") then
                let! xml = toXmlAsync response
                return xml.ToString(SaveOptions.None)
            else
                return! toTextAsync response
        with _ ->
            return! toTextAsync response
    }

let toFormattedTextTAsync response = toFormattedTextAsync response |> Async.StartAsTask
let toFormattedText response = toFormattedTextAsync response |> Async.RunSynchronously


// -----------
// Saving / IO
// -----------

let saveFileAsync (fileName: string) response =
    async {
        let fullFileName = Path.GetFullPath fileName
        let dir = Path.GetDirectoryName fullFileName

        if Directory.Exists dir |> not then
            Directory.CreateDirectory dir |> ignore

        let! stream = response |> toStreamAsync
        do! stream |> Stream.saveFileAsync fileName
    }

let saveFileTAsync (fileName: string) response = saveFileAsync fileName response |> Async.StartAsTask
let saveFile (fileName: string) response = saveFileAsync fileName response |> Async.RunSynchronously


// -----------
// Transformations
// -----------

let toResult (response: Response) =
    match int response.statusCode with
    | code when code >= 200 && code < 300 -> Ok response
    | _ -> Error response

let asOriginalHttpRequestMessage (response: Response) = response.originalHttpRequestMessage

let asOriginalHttpResponseMessage (response: Response) = response.originalHttpResponseMessage


// -----------
// Expect / Assert
// -----------

let expectHttpStatusCodes (codes: HttpStatusCode list) response =
    match set codes |> Set.contains response.statusCode with
    | true -> Ok response
    //| false -> Error (sprintf $"Status code {HttpStatusCode.show r.statusCode} is not in expected [{codes}].")
    | false ->
        Error {
            expected = codes
            actual = response.statusCode
        }

let expectHttpStatusCode (code: HttpStatusCode) = expectHttpStatusCodes [ code ]
let expectStatusCodes (codes: int list) = expectHttpStatusCodes (codes |> List.map LanguagePrimitives.EnumOfValue)
let expectStatusCode (code: int) = expectStatusCodes [ code ]

let assertHttpStatusCodes codes response =
    expectHttpStatusCodes codes response
    |> Result.getValueOrThrow StatusCodeExpectedxception

let assertHttpStatusCode code response = assertHttpStatusCodes [ code ] response

let assertStatusCodes codes response =
    expectStatusCodes codes response
    |> Result.getValueOrThrow StatusCodeExpectedxception

let assertStatusCode code response = assertStatusCodes [ code ] response

let assertOk response = assertStatusCode 200 response
let assertNoContent response = assertStatusCode 204 response
let assertBadRequest response = assertStatusCode 400 response
let assertUnauthorized response = assertStatusCode 401 response
let assertForbidden response = assertStatusCode 403 response
let assertNotFound response = assertStatusCode 404 response
let assert1xx response = assertStatusCodes [ 100..199 ] response
let assert2xx response = assertStatusCodes [ 200..299 ] response
let assert3xx response = assertStatusCodes [ 300..399 ] response
let assert4xx response = assertStatusCodes [ 400..499 ] response
let assert5xx response = assertStatusCodes [ 500..599 ] response
let assert9xx response = assertStatusCodes [ 900..999 ] response
// TODO: Some more explicit expectations


// TODO:
// Multipart extraction
// mime types
// content types
