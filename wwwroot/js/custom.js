function createNewVcInputDiv() {
    var credRequestContainer = document.getElementById("credentialRequestsContainer");
    var container = document.createElement("div");
    container.className = "credRequest";
    var typeContainer = createInputContainer("type", numOfCredentialRequests);
    var contractContainer = createInputContainer("contract", numOfCredentialRequests);
    container.appendChild(typeContainer);
    container.appendChild(contractContainer);
    credRequestContainer.appendChild(container)
  }
  
  function createInputContainer(inputType, inputNumber) {
    var container = document.createElement("div");
    container.className = "inputDiv";
  
    var label = document.createElement("label");
    label.innerHTML = inputType + " " + inputNumber;
    label.className = "input";
  
    var input = document.createElement("input");
    input.className = "input";
    input.id = inputType + inputNumber;
    input.type = "text";
    
    container.appendChild(label);
    container.appendChild(input);
    return container;
  }

  function addVcInput() {
    numOfCredentialRequests++;
    createNewVcInputDiv()
  }

  addVcInput();