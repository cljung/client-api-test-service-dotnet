var numOfCredentialRequests = 1;

const delay = ms => new Promise(res => setTimeout(res, ms));

async function createRequest(isIssuance) {
  var rules = createRequestedRules()
  
  if (!rules.length) {
    alert("No Card Requested.")
    return
  }
  
  var request = {
    rules,
    isIssuance: isIssuance
  }

  console.log(request)
  
  try {
    let response = await fetch('/request', {
      method: "POST",
      headers: {
        "Content-Type": "application/json"
      },
      body: JSON.stringify(request)
    })
    response = await handleServiceErrors(response)
    await handleResponse(await response.json())
  } catch(error) {
    alert("Could not create request. Try Again.")
    console.log(error)
  }
}

function createRequestedRules() {
  let rules = createPresentationFromCheckBoxInputs()
  console.log(rules)
  return rules.concat(createPresentationFromSelectedContracts(), createPresentationFromFreeFormInputs())
}

async function handleResponse(presentationRequestInfo) {
  console.log("PRESENTATION REQUEST INFO")
  console.log(presentationRequestInfo)
  localStorage.setItem("requestId", presentationRequestInfo.requestId)
  document.getElementById("requestId").innerHTML = "Request ID: " + presentationRequestInfo.requestId;
  
  // set up QR code
  var qrcode = new QRCode(qr_output);
  qrcode.makeCode(presentationRequestInfo.requestUrl);
  $('#exampleModal').modal('show')
  $("#exampleModal").on("hidden.bs.modal", function () {
    location.reload()
  });
  
  // set up DeepLink Button
  let deepLinkUrl = presentationRequestInfo.deeplinkUrl
  console.log(deepLinkUrl)
  document.getElementById('deepLinkToWalletButton').setAttribute('href', deepLinkUrl);
}

function createPresentationFromCheckBoxInputs() {
  let selectedRules = []
  let rules = JSON.parse(localStorage.getItem("rules"))
  for (const rule in rules) {
    let ruleCheckboxElement = document.getElementsByName(rule)[0]
    if (ruleCheckboxElement == null) {
      continue;
    } else if (ruleCheckboxElement.checked) {
      selectedRules.push(rules[rule])
    }
  }
  console.log(selectedRules)
  return selectedRules
}

function createPresentationFromFreeFormInputs() {
  var rules = []
  for(var i = 1;i<=numOfCredentialRequests;i++) {
    var typeElement = document.getElementById("type" + i)
    var contractElement = document.getElementById("contract" + i)
    var issuerElement = document.getElementById("issuer" + i)
    if ((typeElement != "" & contractElement != "" & issuerElement != "") & (typeElement != null & contractElement != null & issuerElement != "")) {
      let rule = {
        credentialType: typeElement.value,
        contracts: [contractElement.value],
        issuers: [{iss: issuerElement.value}]
      }
      rules.push(rule)
    }
  }
  return rules
}

function createPresentationFromSelectedContracts() {
  return selectedRules
}

function getRequestToken() {
  
  var id = localStorage.getItem("requestId");
  
  fetch('/request/' + id)
  .then(handleServiceErrors)
  .then((response) => {
    response.text().then((content) => {
      window.open("https://jwt.ms/#id_token=" + content);
      console.log(content)
    })
  }).catch((error) => {
    console.log("Not completed yet.")
    alert("Request not saved properly. Please restart :).")
  });
}

function checkResponse() {
  
  var id = localStorage.getItem("requestId")
  
  fetch('/response/' + id)
  .then(handleServiceErrors)
  .then((response) => {
    response.json().then((content) => {
      validResponse()
      delay(5000).then(() => {
        window.open("https://jwt.ms/#id_token=" + content.token);
        console.log(content)
      })
    })
  }).catch((error) => {
    alert("Presentation Flow Not Completed Yet :)")
    console.log("Not completed yet.")
  });
}

function handleServiceErrors(response) {
  if (response.ok) return response;
  throw response;
}

function validResponse() {
  qr_output.querySelector('img').src = '../images/check.png';
  let valid = document.createElement('p')
  valid.className = "lead small"
  valid.style.color = "green"
  valid.textContent = "Valid Response"
  let container = document.getElementById("requestId")
  container.appendChild(valid)
}

function restart() {
  localStorage.removeItem("requestId");
  location.reload();
  getContracts()
}

function isIssuanceFlow() {
  var isIssuanceFlow = document.getElementsByName("isIssuance")[0]
  return isIssuanceFlow.checked
}

document.getElementById("getRequestTokenButton").disabled = true;