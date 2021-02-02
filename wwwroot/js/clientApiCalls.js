let reloadPage = true

async function createRequest(type) {
    try {
        reloadPage = true
        let endpoint = '/' + type + 'Reference' 
        const response = await fetch(endpoint)
        await handleServiceErrors(response)
        const requestConfig = await response.json()
        localStorage.setItem("request", requestConfig.url)
        localStorage.setItem("pin", requestConfig)
        console.log(requestConfig)
        return requestConfig
        
    } catch (error) {
        console.log("No RequestConfig Found")
    }
}

async function showQRCode(url) {

    if (url == null) {
        url = localStorage.getItem("request")
    }
    
    if (url == null) {
        console.log("request not in storage")
        return
    }
    
    // set up QR code
    var qrcode = new QRCode(qr_output);
    qrcode.makeCode(url);
    $('#exampleModal').modal('show')
    $("#exampleModal").on("hidden.bs.modal", function () {
        document.getElementById("requestUri").innerText = ""
        if (reloadPage) {
            location.reload()
        }
    });

    document.getElementById('deepLinkToWalletButton').setAttribute('href', url);
}

async function createIssuanceRequestAndShowQrCode() {
    let requestConfig = await createRequest("issuance")
    let requestUriReference = requestConfig.url
    document.getElementById("pin").innerText = requestConfig.pin;
    showQRCode(requestUriReference)
}

async function createPresentationRequestAndShowQrCode() {
    let requestConfig = await createRequest("presentation")
    let requestUriReference = requestConfig.url
    showQRCode(requestUriReference)
}

async function getRequestToken() {
    let request = localStorage.getItem("request")

    if (request == null) {
        console.log("request not in storage")
        return
    }

    let requestUrl = new URL(request)
    let params = requestUrl.searchParams
    let request_uri = params.get("request_uri")
    console.log("request_uri=" + request_uri)

    let request_uri_p = document.getElementById("requestUri")
    request_uri_p.innerText = request_uri

    try {
        let response = await fetch(request_uri)
        let token = await response.text()
        console.log("request token=" + token)
        window.open("https://jwt.ms/#id_token=" + token)
    } catch (error) {
        console.log(error)
    }
}

async function getResponse() {
    try {
        const response = await fetch('/checkResponse')
        await handleServiceErrors(response)
        const claims = await response.json()
        console.log(claims)
        let claims_p = document.getElementById("claims")
        claims_p.innerText = JSON.stringify(claims)
        reloadPage = false
        $('#exampleModal').modal('hide')
        $('#buttonDiv').hide()
        $('#claimsDiv').show()
        
    } catch (error) {
        console.log("No Claims Found")
    }
}

function handleServiceErrors(response) {
    if (response.ok) return response;
    throw response;
}